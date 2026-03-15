using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.Kafka;

internal sealed class KafkaOutboxTransport : IOutboxTransport
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaOutboxTransport> _logger;
    private readonly int _sendTimeoutMs;

    public KafkaOutboxTransport(
        IProducer<string, string> producer,
        IOptions<KafkaTransportOptions> options,
        ILogger<KafkaOutboxTransport> logger)
    {
        _producer = producer;
        _logger = logger;
        _sendTimeoutMs = options.Value.SendTimeoutSeconds * 1000;
    }

    public async Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        var deliveryErrors = new List<Exception>();
        var pending = messages.Count;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        foreach (var msg in messages)
        {
            var kafkaMessage = new Message<string, string>
            {
                Key = partitionKey,
                Value = msg.Payload,
                Headers = ParseHeaders(msg.Headers, msg.EventType, msg.SequenceNumber),
            };

            _producer.Produce(topicName, kafkaMessage, report =>
            {
                if (report.Error is { IsError: true })
                {
                    lock (deliveryErrors)
                    {
                        deliveryErrors.Add(new ProduceException<string, string>(
                            report.Error, report));
                    }
                }

                if (Interlocked.Decrement(ref pending) == 0)
                    tcs.TrySetResult();
            });
        }

        // Flush queued messages with cancellation support.
        // Poll in short intervals so we can respect the cancellation token.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_sendTimeoutMs));

        while (!tcs.Task.IsCompleted)
        {
            cts.Token.ThrowIfCancellationRequested();
            _producer.Flush(TimeSpan.FromMilliseconds(100));
        }

        await tcs.Task.ConfigureAwait(false); // propagate any exceptions

        if (deliveryErrors.Count > 0)
        {
            throw new AggregateException(
                $"Failed to deliver {deliveryErrors.Count}/{messages.Count} messages to topic '{topicName}'",
                deliveryErrors);
        }
    }

    public ValueTask DisposeAsync()
    {
        // Don't dispose the producer — it's owned by the DI container.
        // Just flush any remaining messages with a short timeout.
        try { _producer.Flush(TimeSpan.FromSeconds(5)); }
        catch { /* best effort during shutdown */ }
        return ValueTask.CompletedTask;
    }

    private Headers ParseHeaders(string? headersJson, string eventType, long sequenceNumber)
    {
        var headers = new Headers();

        // Always propagate EventType (consistent with EventHub transport)
        headers.Add("EventType", System.Text.Encoding.UTF8.GetBytes(eventType));

        if (!string.IsNullOrEmpty(headersJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (dict is not null)
                {
                    foreach (var kvp in dict)
                        headers.Add(kvp.Key, System.Text.Encoding.UTF8.GetBytes(kvp.Value));
                }
            }
            catch (JsonException ex)
            {
                // Skip corrupted headers — don't crash the entire batch, but log so
                // operators can trace missing headers back to the source message.
                _logger.LogWarning(ex,
                    "Skipping corrupted headers for message {SequenceNumber} — headers will not be propagated",
                    sequenceNumber);
            }
        }

        return headers;
    }
}
