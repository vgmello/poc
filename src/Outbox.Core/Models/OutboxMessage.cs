// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Represents a message stored in the transactional outbox table.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ordering contract:</b> Messages sharing the same <see cref="PartitionKey" /> are
///         delivered to the broker in <see cref="EventDateTimeUtc" /> then <see cref="EventOrdinal" />
///         order. This is enforced by the FetchBatch query's ORDER BY clause and the partition-affinity
///         model (one publisher per logical partition at a time). Callers MUST set both fields
///         correctly at insert time to achieve causal ordering.
///     </para>
///
///     <para>
///         <b>EventOrdinal:</b> A tie-breaker for events that share the same
///         <see cref="EventDateTimeUtc" />. Use sequential values (0, 1, 2, ...) within a single
///         transaction to guarantee deterministic ordering. Stored as SQL INT.
///         Defaults to 0 in the database schema if omitted.
///     </para>
///
///     <para>
///         <b>At-least-once guarantee:</b> Messages may be delivered more than once.
///         Consumers must be idempotent. Consider using <see cref="SequenceNumber" /> as a
///         deduplication key on the consumer side.
///     </para>
///
///     <para>
///         <b>Retry tracking:</b> Retry state is held in process memory by the publisher's
///         in-batch retry loop. There is no persistent retry counter on this record; restarts
///         re-fetch failed messages with a fresh attempt budget.
///     </para>
/// </remarks>
public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    Dictionary<string, string>? Headers,
    byte[] Payload,
    string PayloadContentType,
    DateTimeOffset EventDateTimeUtc,
    int EventOrdinal,
    DateTimeOffset CreatedAtUtc);
