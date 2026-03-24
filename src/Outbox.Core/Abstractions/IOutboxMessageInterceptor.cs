// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

/// <summary>
///     Intercepts outbox messages after retrieval from the store and before transport publishing.
///     Implementations can mutate <see cref="OutboxMessageContext.Payload" />
///     and <see cref="OutboxMessageContext.Headers" /> (dictionary).
///     <para>
///         <b>Failure semantics:</b> If <see cref="InterceptAsync" /> throws, the exception propagates
///         to the group-level error handler. All messages in the same (topic, partitionKey) group will
///         have their retry count incremented — including messages that were already successfully
///         intercepted earlier in the batch. This matches the existing transport-failure semantics.
///     </para>
/// </summary>
public interface IOutboxMessageInterceptor
{
    /// <summary>
    ///     Determines whether this interceptor should run for the given message.
    ///     Called before context allocation — return false to skip without overhead.
    ///     <para>
    ///         <b>Note:</b> The <paramref name="message" /> is always the original message from the store,
    ///         regardless of mutations applied by prior interceptors in the chain. This allows cheap,
    ///         allocation-free filtering. Use <see cref="InterceptAsync" /> to inspect post-mutation state.
    ///     </para>
    /// </summary>
    bool AppliesTo(OutboxMessage message);

    /// <summary>
    ///     Transforms the message data. The context is shared across all interceptors
    ///     in the chain — mutations are visible to subsequent interceptors.
    ///     Return <see cref="ValueTask" /> to allow synchronous interceptors (e.g., header injection)
    ///     to complete without allocating a state machine.
    /// </summary>
    ValueTask InterceptAsync(OutboxMessageContext context, CancellationToken ct);
}
