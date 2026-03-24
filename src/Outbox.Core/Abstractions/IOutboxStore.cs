// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxStore
{
    Task<string> RegisterProducerAsync(CancellationToken ct);
    Task UnregisterProducerAsync(string producerId, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        string producerId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct);

    Task DeletePublishedAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task ReleaseLeaseAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers,
        bool incrementRetry, CancellationToken ct);

    Task DeadLetterAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct);

    Task HeartbeatAsync(string producerId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string producerId, CancellationToken ct);
    Task RebalanceAsync(string producerId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string producerId, CancellationToken ct);

    Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct);

    Task<long> GetPendingCountAsync(CancellationToken ct);
}
