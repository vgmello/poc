// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxStore
{
    /// <summary>
    ///     Stable identity for this publisher instance, generated when the store is constructed.
    ///     Available for logging and correlation before <see cref="RegisterPublisherAsync" /> completes.
    /// </summary>
    string PublisherId { get; }

    Task<string> RegisterPublisherAsync(CancellationToken ct);
    Task UnregisterPublisherAsync(string publisherId, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize,
        int maxRetryCount, CancellationToken ct);

    Task DeletePublishedAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task IncrementRetryCountAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct);

    Task HeartbeatAsync(string publisherId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct);
    Task RebalanceAsync(string publisherId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct);

    Task SweepDeadLettersAsync(string publisherId, int maxRetryCount, CancellationToken ct);

    Task<long> GetPendingCountAsync(CancellationToken ct);
}
