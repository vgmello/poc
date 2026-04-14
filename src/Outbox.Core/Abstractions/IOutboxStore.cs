// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxStore
{
    /// <summary>
    ///     Stable identity for this publisher instance, generated when the store is constructed.
    /// </summary>
    string PublisherId { get; }

    Task<string> RegisterPublisherAsync(CancellationToken ct);
    Task UnregisterPublisherAsync(string publisherId, CancellationToken ct);

    /// <summary>
    ///     Fetches the next batch of pending messages for partitions owned by this
    ///     publisher. No retry-count filter is applied — retry tracking is in-memory.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize, CancellationToken ct);

    Task DeletePublishedAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    /// <summary>
    ///     Atomically deletes the specified messages from the outbox table and inserts
    ///     them into the dead-letter table. <paramref name="attemptCount" /> records
    ///     how many in-memory attempts were made before giving up; it is written to
    ///     the dead-letter table's <c>AttemptCount</c> column.
    /// </summary>
    Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers,
        int attemptCount,
        string? lastError,
        CancellationToken ct);

    Task HeartbeatAsync(string publisherId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct);
    Task RebalanceAsync(string publisherId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct);
    Task<long> GetPendingCountAsync(CancellationToken ct);
}
