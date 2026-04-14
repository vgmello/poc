// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxTransport : IAsyncDisposable
{
    Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Classifies a transport-layer exception as transient (broker is sick — should
    ///     NOT burn an attempt; SHOULD trip the circuit breaker) or non-transient
    ///     (message is bad — SHOULD burn an attempt; should NOT trip the circuit
    ///     breaker). Implementations must inspect their broker SDK's exception types.
    ///     Default for unknown exception types is <c>false</c> (non-transient) — this
    ///     biases toward dead-lettering over infinite retry, which is the safer default
    ///     for an outbox.
    /// </summary>
    bool IsTransient(Exception exception);
}
