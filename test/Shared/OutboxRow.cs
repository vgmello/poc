namespace Outbox.Shared;

public sealed record OutboxRow(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    DateTime EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount = 0);
