// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Why a publish attempt for a (topic, partitionKey) group did not result in
///     full delivery. Passed to <see cref="Abstractions.IOutboxEventHandler.OnPublishFailedAsync" />
///     when the in-batch retry loop exits without successfully delivering every message.
/// </summary>
public enum PublishFailureReason
{
    /// <summary>
    ///     The retry loop exhausted <c>MaxPublishAttempts</c> without success. The
    ///     remaining messages have been moved to the dead-letter table inline. This
    ///     handler call is followed by one <c>OnMessageDeadLetteredAsync</c> per
    ///     dead-lettered message.
    /// </summary>
    RetriesExhausted,

    /// <summary>
    ///     A transient transport failure tripped the topic's circuit breaker before
    ///     retries could exhaust. The remaining messages stay in the outbox and will
    ///     be retried after the circuit closes.
    /// </summary>
    CircuitOpened,

    /// <summary>
    ///     The publisher was shut down while the retry loop was running. The remaining
    ///     messages stay in the outbox.
    ///     <see cref="Abstractions.IOutboxEventHandler.OnPublishFailedAsync" /> is NOT
    ///     called for this reason — only logs are emitted.
    /// </summary>
    Cancelled
}
