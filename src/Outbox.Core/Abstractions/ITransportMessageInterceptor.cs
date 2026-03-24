// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

/// <summary>
///     Intercepts transport-specific message envelopes before they are sent to the broker.
///     Each transport package closes the generic with its envelope type.
/// </summary>
public interface ITransportMessageInterceptor<TMessage>
{
    /// <summary>
    ///     Determines whether this interceptor should run for the given message.
    ///     Called before context allocation.
    /// </summary>
    bool AppliesTo(OutboxMessage message);

    /// <summary>
    ///     Transforms the transport envelope.
    ///     Return <see cref="ValueTask" /> to allow synchronous interceptors to complete without
    ///     allocating a state machine.
    /// </summary>
    ValueTask InterceptAsync(TransportMessageContext<TMessage> context, CancellationToken ct);
}
