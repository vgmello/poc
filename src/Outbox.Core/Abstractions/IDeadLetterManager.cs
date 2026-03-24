// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IDeadLetterManager
{
    Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct);

    Task ReplayAsync(
        IReadOnlyList<long> deadLetterSeqs, CancellationToken ct);

    Task PurgeAsync(
        IReadOnlyList<long> deadLetterSeqs, CancellationToken ct);

    Task PurgeAllAsync(CancellationToken ct);
}
