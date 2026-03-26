// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxEventHandler
{
    Task OnMessagePublishedAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnMessageDeadLetteredAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnPublishFailedAsync(
        IReadOnlyList<OutboxMessage> messages, Exception exception, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnCircuitBreakerStateChangedAsync(
        string topicName, CircuitState state, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnRebalanceAsync(
        string publisherId, IReadOnlyList<int> ownedPartitions, CancellationToken ct) =>
        Task.CompletedTask;
}
