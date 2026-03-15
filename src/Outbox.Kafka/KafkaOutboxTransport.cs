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

        foreach (var msg in messages)
        {
            var kafkaMessage = new Message<string, string>
            {
                Key = partitionKey,
                Value = msg.Payload,
                Headers = ParseHeaders(msg.Headers),
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
            });
        }

        // Flush all queued messages as a single batch.
        // This blocks until all delivery reports have been received.
        int remaining = _producer.Flush(TimeSpan.FromMilliseconds(_sendTimeoutMs));

        if (remaining > 0)
        {
            throw new TimeoutException(
                $"Kafka flush timed out with {remaining} message(s) still in queue for topic '{topicName}'");
        }

        if (deliveryErrors.Count > 0)
        {
            throw new AggregateException(
                $"Failed to deliver {deliveryErrors.Count}/{messages.Count} messages to topic '{topicName}'",
                deliveryErrors);
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }

    private static Headers? ParseHeaders(string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson))
            return null;

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        if (dict is null || dict.Count == 0)
            return null;

        var headers = new Headers();
        foreach (var kvp in dict)
        {
            headers.Add(kvp.Key, System.Text.Encoding.UTF8.GetBytes(kvp.Value));
        }
        return headers;
    }
}
