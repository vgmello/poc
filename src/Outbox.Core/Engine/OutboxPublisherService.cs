using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Engine;

internal sealed class OutboxPublisherService : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxInstrumentation _instrumentation;

    public OutboxPublisherService(
        IOutboxStore store,
        IOutboxTransport transport,
        IOutboxEventHandler eventHandler,
        IOptionsMonitor<OutboxPublisherOptions> options,
        ILogger<OutboxPublisherService> logger,
        OutboxInstrumentation instrumentation)
    {
        _store = store;
        _transport = transport;
        _eventHandler = eventHandler;
        _options = options;
        _logger = logger;
        _instrumentation = instrumentation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store is null)
            throw new InvalidOperationException("IOutboxStore has not been configured. Call UseStore() on the outbox builder.");
        if (_transport is null)
            throw new InvalidOperationException("IOutboxTransport has not been configured. Call UseTransport() on the outbox builder.");

        var opts = _options.CurrentValue;
        var circuitBreaker = new TopicCircuitBreaker(
            opts.CircuitBreakerFailureThreshold,
            opts.CircuitBreakerOpenDurationSeconds);

        var producerId = await _store.RegisterProducerAsync(stoppingToken);
        _logger.LogInformation("Outbox publisher registered as producer {ProducerId}", producerId);

        try
        {
            await Task.WhenAll(
                PublishLoopAsync(producerId, circuitBreaker, stoppingToken),
                HeartbeatLoopAsync(producerId, stoppingToken),
                RebalanceLoopAsync(producerId, stoppingToken),
                OrphanSweepLoopAsync(producerId, stoppingToken),
                DeadLetterSweepLoopAsync(stoppingToken));
        }
        finally
        {
            await _store.UnregisterProducerAsync(producerId, CancellationToken.None);
            _logger.LogInformation("Outbox publisher unregistered producer {ProducerId}", producerId);
        }
    }

    private async Task PublishLoopAsync(
        string producerId, TopicCircuitBreaker circuitBreaker, CancellationToken ct)
    {
        var pollIntervalMs = _options.CurrentValue.MinPollIntervalMs;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;
                var pollSw = Stopwatch.StartNew();

                var batch = await _store.LeaseBatchAsync(
                    producerId, opts.BatchSize, opts.LeaseDurationSeconds,
                    opts.MaxRetryCount, ct);

                pollSw.Stop();
                _instrumentation.PollDuration.Record(pollSw.Elapsed.TotalMilliseconds);
                _instrumentation.BatchSize.Record(batch.Count);

                if (batch.Count == 0)
                {
                    pollIntervalMs = Math.Min(pollIntervalMs * 2, opts.MaxPollIntervalMs);
                    await Task.Delay(pollIntervalMs, ct);
                    continue;
                }

                pollIntervalMs = opts.MinPollIntervalMs;

                // Separate poison messages
                var poison = batch.Where(m => m.RetryCount >= opts.MaxRetryCount).ToList();
                var healthy = batch.Where(m => m.RetryCount < opts.MaxRetryCount).ToList();

                // Dead-letter poison messages
                if (poison.Count > 0)
                {
                    await _store.DeadLetterAsync(
                        poison.Select(m => m.SequenceNumber).ToList(),
                        "Max retry count exceeded",
                        ct);

                    _instrumentation.MessagesDeadLettered.Add(poison.Count);

                    foreach (var msg in poison)
                    {
                        await _eventHandler.OnMessageDeadLetteredAsync(msg, ct);
                    }
                }

                // Group healthy messages by (TopicName, PartitionKey)
                var groups = healthy
                    .GroupBy(m => (m.TopicName, m.PartitionKey))
                    .ToList();

                foreach (var group in groups)
                {
                    var topicName = group.Key.TopicName;
                    var partitionKey = group.Key.PartitionKey;
                    var groupMessages = group.ToList();
                    var sequenceNumbers = groupMessages.Select(m => m.SequenceNumber).ToList();

                    if (circuitBreaker.IsOpen(topicName))
                    {
                        await _store.ReleaseLeaseAsync(producerId, sequenceNumbers, ct);
                        continue;
                    }

                    try
                    {
                        var publishSw = Stopwatch.StartNew();

                        using var activity = _instrumentation.ActivitySource.StartActivity("outbox.publish");
                        activity?.SetTag("messaging.destination.name", topicName);
                        activity?.SetTag("messaging.batch.message_count", groupMessages.Count);

                        await _transport.SendAsync(topicName, partitionKey, groupMessages, ct);

                        publishSw.Stop();
                        _instrumentation.PublishDuration.Record(publishSw.Elapsed.TotalMilliseconds);

                        await _store.DeletePublishedAsync(producerId, sequenceNumbers, ct);
                        _instrumentation.MessagesPublished.Add(groupMessages.Count);

                        var (stateChanged, newState) = circuitBreaker.RecordSuccess(topicName);
                        if (stateChanged)
                        {
                            await _eventHandler.OnCircuitBreakerStateChangedAsync(topicName, newState, ct);
                            _instrumentation.CircuitBreakerStateChanges.Add(1);
                        }

                        foreach (var msg in groupMessages)
                        {
                            await _eventHandler.OnMessagePublishedAsync(msg, ct);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to publish {Count} messages to topic {Topic}", groupMessages.Count, topicName);
                        _instrumentation.PublishFailures.Add(1);

                        await _store.ReleaseLeaseAsync(producerId, sequenceNumbers, ct);

                        var previousState = circuitBreaker.GetState(topicName);
                        var (stateChanged, newState) = circuitBreaker.RecordFailure(topicName);

                        await _eventHandler.OnPublishFailedAsync(groupMessages, ex, ct);

                        if (stateChanged)
                        {
                            await _eventHandler.OnCircuitBreakerStateChangedAsync(topicName, newState, ct);
                            _instrumentation.CircuitBreakerStateChanges.Add(1);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in publish loop");
            }
        }
    }

    private async Task HeartbeatLoopAsync(string producerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.HeartbeatIntervalMs, ct);
                await _store.HeartbeatAsync(producerId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat loop");
            }
        }
    }

    private async Task RebalanceLoopAsync(string producerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.RebalanceIntervalMs, ct);
                await _store.RebalanceAsync(producerId, ct);
                var ownedPartitions = await _store.GetOwnedPartitionsAsync(producerId, ct);
                await _eventHandler.OnRebalanceAsync(producerId, ownedPartitions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in rebalance loop");
            }
        }
    }

    private async Task OrphanSweepLoopAsync(string producerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.OrphanSweepIntervalMs, ct);
                await _store.ClaimOrphanPartitionsAsync(producerId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in orphan sweep loop");
            }
        }
    }

    private async Task DeadLetterSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.DeadLetterSweepIntervalMs, ct);
                await _store.SweepDeadLettersAsync(_options.CurrentValue.MaxRetryCount, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in dead letter sweep loop");
            }
        }
    }
}
