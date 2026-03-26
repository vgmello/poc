// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxStore
{
    Task<string> RegisterPublisherAsync(CancellationToken ct);
    Task UnregisterPublisherAsync(string publisherId, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        string publisherId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct);

    Task DeletePublishedAsync(
        string publisherId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task ReleaseLeaseAsync(
        string publisherId, IReadOnlyList<long> sequenceNumbers,
        bool incrementRetry, CancellationToken ct);

    Task DeadLetterAsync(
        string publisherId, IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct);

    Task HeartbeatAsync(string publisherId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct);
    Task RebalanceAsync(string publisherId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct);

    Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct);

    Task<long> GetPendingCountAsync(CancellationToken ct);
}
