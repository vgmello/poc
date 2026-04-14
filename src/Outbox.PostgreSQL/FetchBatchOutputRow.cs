// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.PostgreSQL;

/// <summary>
///     Dapper materialization target for the FetchBatch query. Maps snake_case columns
///     to mutable properties, then projects into the immutable <see cref="OutboxMessage" />
///     domain model so the domain record does not need a parameterless constructor.
/// </summary>
internal sealed class FetchBatchOutputRow
{
    public long SequenceNumber { get; set; }
    public string TopicName { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Dictionary<string, string>? Headers { get; set; }
    public byte[] Payload { get; set; } = [];
    public string PayloadContentType { get; set; } = string.Empty;
    public DateTimeOffset EventDateTimeUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public OutboxMessage ToDomain() =>
        new(
            SequenceNumber,
            TopicName,
            PartitionKey,
            EventType,
            Headers,
            Payload,
            PayloadContentType,
            EventDateTimeUtc,
            CreatedAtUtc);
}
