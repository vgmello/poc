using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Engine;

internal sealed class OutboxPublisherService : BackgroundService
{
    private const int MaxConsecutiveRestarts = 5;
    private static readonly TimeSpan RestartBaseDelay = TimeSpan.FromSeconds(2);

    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxInstrumentation _instrumentation;
    private readonly OutboxHealthState _healthState;
    private readonly IHostApplicationLifetime _appLifetime;

    public OutboxPublisherService(
        IOutboxStore store,
        IOutboxTransport transport,
        IOutboxEventHandler eventHandler,
        IOptionsMonitor<OutboxPublisherOptions> options,
        ILogger<OutboxPublisherService> logger,
        OutboxInstrumentation instrumentation,
        OutboxHealthState healthState,
        IHostApplicationLifetime appLifetime)
    {
        _store = store;
        _transport = transport;
        _eventHandler = eventHandler;
        _options = options;
        _logger = logger;
        _instrumentation = instrumentation;
        _healthState = healthState;
        _appLifetime = appLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        LogConfigurationSummary(opts);

        var circuitBreaker = new TopicCircuitBreaker(
            opts.CircuitBreakerFailureThreshold,
            opts.CircuitBreakerOpenDurationSeconds);

        string producerId = null!;
        for (int attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                producerId = await _store.RegisterProducerAsync(stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, attempt - 1), 60));
                _logger.LogError(ex,
                    "Failed to register outbox producer (attempt {Attempt}), retrying in {Delay:F0}s",
                    attempt, delay.TotalSeconds);
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        _logger.LogInformation("Outbox publisher registered as producer {ProducerId}", producerId);

        _instrumentation.RegisterPendingGauge();

        try
        {
            await RunLoopsWithRestartAsync(producerId, circuitBreaker, stoppingToken);
        }
        finally
        {
            _healthState.SetPublishLoopRunning(false);

            try
            {
                await _store.UnregisterProducerAsync(producerId, CancellationToken.None);
                _logger.LogInformation("Outbox publisher unregistered producer {ProducerId}", producerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unregister producer {ProducerId} during shutdown", producerId);
            }
        }
    }

    /// <summary>
    /// Runs all loops with a linked CancellationTokenSource. If any loop exits
    /// unexpectedly, all loops are cancelled and restarted with exponential backoff.
    /// After <see cref="MaxConsecutiveRestarts"/> consecutive failures, the host is stopped.
    /// </summary>
    private async Task RunLoopsWithRestartAsync(
        string producerId, TopicCircuitBreaker circuitBreaker, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var ct = linkedCts.Token;

            _healthState.SetPublishLoopRunning(true);

            try
            {
                var tasks = new[]
                {
                    PublishLoopAsync(producerId, circuitBreaker, ct),
                    HeartbeatLoopAsync(producerId, ct),
                    RebalanceLoopAsync(producerId, ct),
                    OrphanSweepLoopAsync(producerId, ct),
                    DeadLetterSweepLoopAsync(ct),
                };

                // Wait for the first task to complete (success or failure).
                var completed = await Task.WhenAny(tasks);

                // A loop exited — cancel the others.
                await linkedCts.CancelAsync();

                // Wait for all to finish cleanup (ignore cancellation exceptions).
                try { await Task.WhenAll(tasks); }
                catch (Exception ex) when (
                    ex is OperationCanceledException ||
                    (ex is AggregateException agg &&
                     agg.InnerExceptions.All(e => e is OperationCanceledException)))
                { /* expected — we just cancelled them */ }

                // If the host is stopping, exit cleanly.
                if (stoppingToken.IsCancellationRequested)
                    return;

                // A loop exited unexpectedly. Check if it faulted.
                if (completed.IsFaulted)
                {
                    _logger.LogError(completed.Exception!.InnerException,
                        "Outbox loop crashed unexpectedly, will attempt restart");
                }
                else
                {
                    _logger.LogWarning("Outbox loop exited unexpectedly without error, will attempt restart");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in outbox loop orchestration");
                if (stoppingToken.IsCancellationRequested)
                    return;
            }

            _healthState.SetPublishLoopRunning(false);
            _healthState.RecordLoopRestart();
            int restarts = _healthState.ConsecutiveLoopRestarts;

            if (restarts >= MaxConsecutiveRestarts)
            {
                _logger.LogCritical(
                    "Outbox loops have restarted {Count} consecutive times — stopping host",
                    restarts);
                _appLifetime.StopApplication();
                return;
            }

            // Exponential backoff before restart, capped at 2 minutes.
            var delay = TimeSpan.FromTicks((long)(RestartBaseDelay.Ticks * Math.Pow(2, restarts - 1)));
            if (delay > TimeSpan.FromMinutes(2))
                delay = TimeSpan.FromMinutes(2);
            _logger.LogWarning(
                "Restarting outbox loops in {Delay:F1}s (attempt {Attempt}/{Max})",
                delay.TotalSeconds, restarts, MaxConsecutiveRestarts);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task PublishLoopAsync(
        string producerId, TopicCircuitBreaker circuitBreaker, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var pollIntervalMs = opts.MinPollIntervalMs;

        // Reset restart counter after sustained healthy operation (30s of successful polling
        // without any loop crashes), rather than on the very first poll.
        var loopStartedAt = Environment.TickCount64;
        const long healthyRunThresholdMs = 30_000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                opts = _options.CurrentValue;
                var pollSw = Stopwatch.StartNew();

                var batch = await _store.LeaseBatchAsync(
                    producerId, opts.BatchSize, opts.LeaseDurationSeconds,
                    opts.MaxRetryCount, ct);

                pollSw.Stop();
                _instrumentation.PollDuration.Record(pollSw.Elapsed.TotalMilliseconds);
                _instrumentation.BatchSize.Record(batch.Count);
                _healthState.RecordPoll();

                if (_healthState.ConsecutiveLoopRestarts > 0 &&
                    Environment.TickCount64 - loopStartedAt >= healthyRunThresholdMs)
                {
                    _healthState.ResetLoopRestarts();
                }

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
                        producerId,
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

                // BUG-5 fix: Track unprocessed sequences for graceful drain on cancellation.
                var unprocessedSequences = new HashSet<long>(
                    healthy.Select(m => m.SequenceNumber));

                var publishedAny = false;

                try
                {
                    foreach (var group in groups)
                    {
                        var topicName = group.Key.TopicName;
                        var partitionKey = group.Key.PartitionKey;
                        var groupMessages = group.OrderBy(m => m.SequenceNumber).ToList();
                        var sequenceNumbers = groupMessages.Select(m => m.SequenceNumber).ToList();

                        if (circuitBreaker.IsOpen(topicName))
                        {
                            // Circuit open — release without incrementing retry count.
                            await _store.ReleaseLeaseAsync(producerId, sequenceNumbers,
                                incrementRetry: false, ct);
                            foreach (var sn in sequenceNumbers)
                                unprocessedSequences.Remove(sn);
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

                            // Transport succeeded — record success metrics before attempting delete.
                            _instrumentation.MessagesPublished.Add(groupMessages.Count);
                            _healthState.RecordSuccessfulPublish();
                            publishedAny = true;

                            foreach (var sn in sequenceNumbers)
                                unprocessedSequences.Remove(sn);

                            var (stateChanged, newState) = circuitBreaker.RecordSuccess(topicName);
                            if (stateChanged)
                            {
                                _healthState.SetCircuitClosed(topicName);
                                await _eventHandler.OnCircuitBreakerStateChangedAsync(topicName, newState, ct);
                                _instrumentation.CircuitBreakerStateChanges.Add(1);
                            }

                            foreach (var msg in groupMessages)
                            {
                                await _eventHandler.OnMessagePublishedAsync(msg, ct);
                            }

                            // Delete from outbox — separate try since transport already succeeded.
                            try
                            {
                                await _store.DeletePublishedAsync(producerId, sequenceNumbers, ct);
                            }
                            catch (Exception deleteEx)
                            {
                                _logger.LogWarning(deleteEx,
                                    "Failed to delete {Count} published messages — they will be re-delivered on next poll",
                                    sequenceNumbers.Count);
                                // Release WITHOUT retry increment — transport succeeded.
                                try
                                {
                                    await _store.ReleaseLeaseAsync(producerId, sequenceNumbers,
                                        incrementRetry: false, CancellationToken.None);
                                }
                                catch (Exception releaseEx)
                                {
                                    _logger.LogWarning(releaseEx,
                                        "Failed to release lease for {Count} published messages — leases will expire naturally",
                                        sequenceNumbers.Count);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Graceful shutdown — don't rethrow yet, let finally release leases.
                            break;
                        }
                        catch (Exception ex)
                        {
                            // Transport failure — increment retry count.
                            _logger.LogError(ex, "Failed to publish {Count} messages to topic {Topic}", groupMessages.Count, topicName);
                            _instrumentation.PublishFailures.Add(1);

                            // Use CancellationToken.None — this must complete even during shutdown
                            // so the retry count is correctly incremented.
                            try
                            {
                                await _store.ReleaseLeaseAsync(producerId, sequenceNumbers,
                                    incrementRetry: true, CancellationToken.None);
                            }
                            catch (Exception releaseEx)
                            {
                                _logger.LogWarning(releaseEx,
                                    "Failed to release lease with retry increment for {Count} messages — they will expire after lease duration",
                                    sequenceNumbers.Count);
                            }

                            foreach (var sn in sequenceNumbers)
                                unprocessedSequences.Remove(sn);

                            var (stateChanged, newState) = circuitBreaker.RecordFailure(topicName);

                            // Update health state before event handler to avoid skipping on handler exception.
                            if (stateChanged)
                            {
                                _healthState.SetCircuitOpen(topicName);
                                _instrumentation.CircuitBreakerStateChanges.Add(1);
                            }

                            await _eventHandler.OnPublishFailedAsync(groupMessages, ex, ct);

                            if (stateChanged)
                            {
                                await _eventHandler.OnCircuitBreakerStateChangedAsync(topicName, newState, ct);
                            }
                        }
                    }
                }
                finally
                {
                    // BUG-5 fix: Release any leases not yet processed (e.g., on cancellation).
                    if (unprocessedSequences.Count > 0)
                    {
                        try
                        {
                            await _store.ReleaseLeaseAsync(
                                producerId,
                                unprocessedSequences.ToList(),
                                incrementRetry: false,
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to release {Count} leases during shutdown — they will expire after lease duration",
                                unprocessedSequences.Count);
                        }
                    }
                }

                // If we leased messages but none were actually published (e.g., all circuits
                // open or all errored), apply adaptive backoff to avoid a hot loop.
                if (!publishedAny && batch.Count > 0)
                {
                    pollIntervalMs = Math.Min(pollIntervalMs * 2, opts.MaxPollIntervalMs);
                    try { await Task.Delay(pollIntervalMs, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }

                // If cancellation was requested during group processing, exit now.
                if (ct.IsCancellationRequested)
                    break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in publish loop");
                try
                {
                    await Task.Delay(_options.CurrentValue.MaxPollIntervalMs, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
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
                _healthState.RecordHeartbeat();

                // Also update pending count metric while we're at it.
                try
                {
                    var pending = await _store.GetPendingCountAsync(ct);
                    _instrumentation.UpdatePendingCount(pending);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to query pending message count");
                }
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

    private void LogConfigurationSummary(OutboxPublisherOptions opts)
    {
        _logger.LogInformation(
            "Outbox publisher starting with configuration: " +
            "BatchSize={BatchSize}, LeaseDuration={LeaseDuration}s, MaxRetry={MaxRetry}, " +
            "Poll={MinPoll}-{MaxPoll}ms, Heartbeat={HbInterval}ms/timeout={HbTimeout}s, " +
            "GracePeriod={Grace}s, CircuitBreaker={CbThreshold}failures/{CbDuration}s",
            opts.BatchSize,
            opts.LeaseDurationSeconds,
            opts.MaxRetryCount,
            opts.MinPollIntervalMs,
            opts.MaxPollIntervalMs,
            opts.HeartbeatIntervalMs,
            opts.HeartbeatTimeoutSeconds,
            opts.PartitionGracePeriodSeconds,
            opts.CircuitBreakerFailureThreshold,
            opts.CircuitBreakerOpenDurationSeconds);
    }
}
