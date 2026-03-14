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
        foreach (var msg in messages)
        {
            var kafkaMessage = new Message<string, string>
            {
                Key = partitionKey,
                Value = msg.Payload,
                Headers = ParseHeaders(msg.Headers),
            };

            using var timeoutCts = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_sendTimeoutMs);

            await _producer.ProduceAsync(topicName, kafkaMessage, timeoutCts.Token);
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
