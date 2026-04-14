// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Represents a message stored in the transactional outbox table.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ordering contract:</b> Messages sharing the same <see cref="PartitionKey" /> are
///         delivered to the broker in <see cref="SequenceNumber" /> order, which equals the
///         order in which they were INSERTed into the outbox table. <b>Callers MUST insert
///         messages in the order they want them delivered.</b> The publisher fetches in
///         <c>sequence_number</c> order within each partition; there is no caller-side override.
///     </para>
///
///     <para>
///         <b>EventDateTimeUtc:</b> Caller-asserted business timestamp. Stored on the row and
///         carried through to the dead-letter table for forensics. <b>Not consulted by the
///         publisher's sort path</b> — it exists for debugging and observability only.
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
    DateTimeOffset CreatedAtUtc);
