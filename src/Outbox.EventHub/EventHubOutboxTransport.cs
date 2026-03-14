using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.EventHub;

internal sealed class EventHubOutboxTransport : IOutboxTransport
{
    private readonly EventHubProducerClient _client;
    private readonly ILogger<EventHubOutboxTransport> _logger;
    private readonly int _sendTimeoutSeconds;

    public EventHubOutboxTransport(
        EventHubProducerClient client,
        IOptions<EventHubTransportOptions> options,
        ILogger<EventHubOutboxTransport> logger)
    {
        _client = client;
        _logger = logger;
        _sendTimeoutSeconds = options.Value.SendTimeoutSeconds;
    }

    public async Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_sendTimeoutSeconds));
        var ct = timeoutCts.Token;

        var batchOptions = new CreateBatchOptions { PartitionKey = partitionKey };
        var batch = await _client.CreateBatchAsync(batchOptions, ct);

        foreach (var msg in messages)
        {
            var eventData = new EventData(msg.Payload);

            if (!string.IsNullOrEmpty(msg.Headers))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(msg.Headers);
                if (headers is not null)
                {
                    foreach (var kvp in headers)
                        eventData.Properties[kvp.Key] = kvp.Value;
                }
            }

            eventData.Properties["EventType"] = msg.EventType;

            if (!batch.TryAdd(eventData))
            {
                // Current batch is full, send it and start a new one
                if (batch.Count > 0)
                {
                    await _client.SendAsync(batch, ct);
                    batch.Dispose();
                }

                batch = await _client.CreateBatchAsync(batchOptions, ct);

                if (!batch.TryAdd(eventData))
                {
                    batch.Dispose();
                    throw new InvalidOperationException(
                        $"Message {msg.SequenceNumber} is too large for an EventHub batch");
                }
            }
        }

        if (batch.Count > 0)
        {
            await _client.SendAsync(batch, ct);
        }

        batch.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}
