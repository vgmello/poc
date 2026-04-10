// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Mutable context wrapping a transport-specific message envelope.
///     Passed to <see cref="Abstractions.ITransportMessageInterceptor{TEnvelope}" />.
/// </summary>
public sealed class TransportMessageContext<TEnvelope> where TEnvelope : class
{
    /// <summary>
    ///     The original outbox message (post-core-interception).
    /// </summary>
    public OutboxMessage OriginalMessage { get; }

    /// <summary>
    ///     The transport-specific envelope. Interceptors may replace or mutate this.
    /// </summary>
    public TEnvelope Envelope { get; set; }

    public TransportMessageContext(OutboxMessage originalMessage, TEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(originalMessage);
        ArgumentNullException.ThrowIfNull(envelope);

        OriginalMessage = originalMessage;
        Envelope = envelope;
    }
}
