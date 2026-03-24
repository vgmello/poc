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
    private readonly ConcurrentDictionary<string, EventHubProducerClient> _clients = new();
    private readonly EventHubClientFactory _clientFactory;
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
        var client = _clients.GetOrAdd(topicName, name => _clientFactory(name));

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
                        eventData = transportCtx.Message;
                    }
                }

                if (!batch.TryAdd(eventData))
                {
                    // Current batch is full, send it and start a new one.
                    if (batch.Count > 0)
                    {
                        await client.SendAsync(batch, ct);
                        sentSequenceNumbers.AddRange(currentBatchSequenceNumbers);
                        batch.Dispose();
                        batch = null;
                        currentBatchSequenceNumbers.Clear();
                    }

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
            var failedSequences = messages
                .Select(m => m.SequenceNumber)
                .Where(sn => !sentSequenceNumbers.Contains(sn))
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

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            try { await client.CloseAsync(); }
            catch
            {
                /* best effort during shutdown */
            }
        }

        _clients.Clear();
    }
}
