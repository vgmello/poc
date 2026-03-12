namespace Outbox.Shared;

public sealed record OutboxRow(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    int RetryCount = 0);
