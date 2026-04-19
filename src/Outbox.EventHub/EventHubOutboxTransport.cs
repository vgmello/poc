// Copyright (c) OrgName. All rights reserved.

using System.Collections.Concurrent;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.EventHub;

internal sealed class EventHubOutboxTransport : IOutboxTransport
{
    private readonly ConcurrentDictionary<string, Lazy<EventHubProducerClient>> _clients = new();
    private readonly EventHubClientFactory _clientFactory;
    private readonly ILogger<EventHubOutboxTransport> _logger;
    private readonly int _sendTimeoutSeconds;
    private readonly int _maxBatchSizeBytes;
    private readonly List<ITransportMessageInterceptor<EventData>> _interceptors;

    public EventHubOutboxTransport(
        IOptions<EventHubTransportOptions> options,
        ILogger<EventHubOutboxTransport> logger,
        IEnumerable<ITransportMessageInterceptor<EventData>> interceptors,
        EventHubClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
        _logger = logger;
        _sendTimeoutSeconds = options.Value.SendTimeoutSeconds;
        _maxBatchSizeBytes = options.Value.MaxBatchSizeBytes;
        _interceptors = interceptors.ToList();
    }

    // Cognitive complexity is inherent to the batch-split-retry-partial-send flow.
    // Splitting further would obscure the EventDataBatch lifecycle management.
#pragma warning disable S3776
    public async Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        // ConcurrentDictionary.GetOrAdd can invoke the value factory more than once when multiple
        // threads race on the same absent key — only one returned value is stored; the rest are
        // abandoned. A raw EventHubProducerClient factory would leak AMQP connections on each lost
        // race (abandoned clients never appear in _clients.Values, so DisposeAsync can't close them).
        // Lazy<T> with ExecutionAndPublication guarantees the real client is constructed exactly once.
        var client = _clients.GetOrAdd(topicName,
            name => new Lazy<EventHubProducerClient>(
                () => _clientFactory(name),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        var batchOptions = new CreateBatchOptions
        {
            PartitionKey = partitionKey,
            MaximumSizeInBytes = _maxBatchSizeBytes > 0 ? _maxBatchSizeBytes : null
        };
        EventDataBatch? batch = null;
        var sentSequenceNumbers = new List<long>();
        var currentBatchSequenceNumbers = new List<long>();

        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCts.CancelAfter(TimeSpan.FromSeconds(_sendTimeoutSeconds));
            var ct = sendCts.Token;

            batch = await client.CreateBatchAsync(batchOptions, ct);

            foreach (var msg in messages)
            {
                var eventData = EventHubMessageHelper.CreateEventData(msg);

                if (_interceptors.Count > 0)
                {
                    TransportMessageContext<EventData>? transportCtx = null;

                    foreach (var interceptor in _interceptors)
                    {
                        if (interceptor.AppliesTo(msg))
                        {
                            transportCtx ??= new TransportMessageContext<EventData>(msg, eventData);
                            await interceptor.InterceptAsync(transportCtx, ct);
                        }
                    }

                    if (transportCtx is not null)
                    {
                        eventData = transportCtx.Envelope;
                    }
                }

                if (!batch.TryAdd(eventData))
                {
                    // Current batch is full, send it and start a new one.
                    if (batch.Count > 0)
                    {
                        await client.SendAsync(batch, ct);
                        sentSequenceNumbers.AddRange(currentBatchSequenceNumbers);
                        currentBatchSequenceNumbers.Clear();
                    }

                    batch.Dispose();

                    // Reset timeout for remaining messages.
                    sendCts.CancelAfter(TimeSpan.FromSeconds(_sendTimeoutSeconds));
                    batch = await client.CreateBatchAsync(batchOptions, ct);

                    if (!batch.TryAdd(eventData))
                    {
                        throw new InvalidOperationException(
                            $"Message {msg.SequenceNumber} is too large for an EventHub batch");
                    }
                }

                currentBatchSequenceNumbers.Add(msg.SequenceNumber);
            }

            if (batch is { Count: > 0 })
            {
                await client.SendAsync(batch, ct);
                sentSequenceNumbers.AddRange(currentBatchSequenceNumbers);
            }
        }
        catch (Exception ex) when (sentSequenceNumbers.Count > 0 && ex is not PartialSendException)
        {
            var sentSet = sentSequenceNumbers.ToHashSet();
            var failedSequences = messages
                .Select(m => m.SequenceNumber)
                .Where(sn => !sentSet.Contains(sn))
                .ToList();

            throw new PartialSendException(
                sentSequenceNumbers, failedSequences,
                $"Partial send: {sentSequenceNumbers.Count} sent, {failedSequences.Count} failed for EventHub",
                ex);
        }
        finally
        {
            batch?.Dispose();
        }
    }

#pragma warning restore S3776

    /// <inheritdoc />
    public bool IsTransient(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => true,
            // The CancelAfter timeout in SendAsync surfaces as OperationCanceledException via
        // the linked CancellationToken, not as TimeoutException. This arm is kept as a
        // defensive guard for SDK versions or interceptors that may raise TimeoutException directly.
        TimeoutException => true,
            EventHubsException eh => eh.IsTransient,
            AggregateException agg => agg.InnerExceptions.Count > 0
                && agg.InnerExceptions.All(IsTransient),
            _ => false
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _clients.Values)
        {
            if (!lazy.IsValueCreated) continue;

            try { await lazy.Value.CloseAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing EventHubProducerClient during shutdown");
            }
        }

        _clients.Clear();
    }
}
