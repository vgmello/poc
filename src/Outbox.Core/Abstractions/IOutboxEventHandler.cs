// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxEventHandler
{
    Task OnMessagePublishedAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnMessageDeadLetteredAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    ///     Fires once per <c>(topic, partitionKey)</c> group when the in-batch retry
    ///     loop exits without fully delivering every message. The <paramref name="reason" />
    ///     identifies which exit path was taken. Per-attempt failures inside the retry
    ///     loop are observable via metrics and logs; this handler is outcome-level only.
    /// </summary>
    Task OnPublishFailedAsync(
        IReadOnlyList<OutboxMessage> messages,
        Exception lastError,
        PublishFailureReason reason,
        CancellationToken ct) =>
        Task.CompletedTask;

    Task OnCircuitBreakerStateChangedAsync(
        string topicName, CircuitState state, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnRebalanceAsync(
        string publisherId, IReadOnlyList<int> ownedPartitions, CancellationToken ct) =>
        Task.CompletedTask;
}
