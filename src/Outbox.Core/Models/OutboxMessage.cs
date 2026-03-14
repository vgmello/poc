namespace Outbox.Core.Models;

public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    DateTimeOffset EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount,
    DateTimeOffset CreatedAtUtc);
