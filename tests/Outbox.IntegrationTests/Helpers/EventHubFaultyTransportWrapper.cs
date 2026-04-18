// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.IntegrationTests.Helpers;

/// <summary>
///     IOutboxTransport decorator for EventHub integration tests. Wraps a real
///     EventHubOutboxTransport and adds the same fault-injection surface as
///     FaultyTransportWrapper does for the Kafka side. Successful sends delegate
///     to the inner transport so its batch-split-retry-partial-send logic is
///     exercised end-to-end.
/// </summary>
public sealed class EventHubFaultyTransportWrapper : IOutboxTransport
{
    private readonly IOutboxTransport _inner;
    private volatile bool _failing;
    private int _callCount;
    private volatile int _failEveryN;
    private volatile int _failAfterN = -1;
    private volatile Func<OutboxMessage, bool>? _failPredicate;
    private volatile bool _simulatedFailuresAreTransient = true;

    public EventHubFaultyTransportWrapper(IOutboxTransport inner) => _inner = inner;

    /// <summary>Total number of SendAsync calls made.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Set to true to make all sends throw.</summary>
    public void SetFailing(bool failing) => _failing = failing;

    /// <summary>Fail all calls except every Nth. Set 0 to disable.</summary>
    public void SetIntermittent(int failEveryN) => _failEveryN = failEveryN;

    /// <summary>Fail sends where any message matches the predicate.</summary>
    public void SetIntermittentPredicate(Func<OutboxMessage, bool> predicate) =>
        _failPredicate = predicate;

    /// <summary>
    ///     Succeed the first N calls, fail every call after that. Used by PartialSendTests
    ///     to force the transport into a partial-send state mid-batch. Set -1 to disable.
    /// </summary>
    public void SetFailingAfterNSuccesses(int n) => _failAfterN = n;

    /// <summary>
    ///     Controls whether simulated failures classify as transient.
    ///     Default: true (simulates broker outages — no DLQ).
    /// </summary>
    public void SetSimulatedFailuresTransient(bool transient) =>
        _simulatedFailuresAreTransient = transient;

    public void Reset()
    {
        _failing = false;
        _failEveryN = 0;
        _failAfterN = -1;
        _failPredicate = null;
        _simulatedFailuresAreTransient = true;
        Interlocked.Exchange(ref _callCount, 0);
    }

    public bool IsTransient(Exception exception)
    {
        if (exception is InvalidOperationException)
            return _simulatedFailuresAreTransient;

        return _inner.IsTransient(exception);
    }

    public async Task SendAsync(
        string topicName, string partitionKey,
        IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _callCount);

        if (_failing)
            throw new InvalidOperationException("Simulated broker failure");

        var failAfter = _failAfterN;
        if (failAfter >= 0 && count > failAfter)
            throw new InvalidOperationException(
                $"Simulated fail-after-N (call {count}, succeed first {failAfter})");

        var pred = _failPredicate;
        if (pred != null && messages.Any(pred))
            throw new InvalidOperationException("Simulated failure for matching message");

        var n = _failEveryN;
        if (n > 0 && count % n != 0)
            throw new InvalidOperationException(
                $"Simulated intermittent failure (call {count}, succeeds every {n})");

        await _inner.SendAsync(topicName, partitionKey, messages, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
