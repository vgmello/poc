// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Mutable context wrapping a transport-specific message envelope.
///     Passed to <see cref="Abstractions.ITransportMessageInterceptor{TMessage}" />.
/// </summary>
public sealed class TransportMessageContext<TMessage>
{
    /// <summary>
    ///     The source outbox message (post-core-interception).
    /// </summary>
    public OutboxMessage SourceMessage { get; }

    /// <summary>
    ///     The transport-specific envelope. Interceptors may replace or mutate this.
    /// </summary>
    public TMessage Message { get; set; }

    public TransportMessageContext(OutboxMessage sourceMessage, TMessage message)
    {
        SourceMessage = sourceMessage;
        Message = message;
    }
}
