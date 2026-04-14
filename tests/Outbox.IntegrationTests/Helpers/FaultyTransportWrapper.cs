// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.IntegrationTests.Helpers;

/// <summary>
///     IOutboxTransport decorator that can be toggled to simulate broker failures.
///     When not failing, delegates to a real Kafka producer.
/// </summary>
public sealed class FaultyTransportWrapper : IOutboxTransport
{
    private readonly IProducer<string, byte[]> _producer;
    private volatile bool _failing;
    private int _callCount;
    private volatile int _failEveryN; // 0 = disabled; N = fail all except every Nth call
    private volatile Func<OutboxMessage, bool>? _failPredicate;
    private volatile bool _simulatedFailuresAreTransient = true;

    public FaultyTransportWrapper(IProducer<string, byte[]> producer) => _producer = producer;

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
    ///     Controls whether simulated failures are classified as transient.
    ///     Default: true (simulates broker outages — no DLQ).
    ///     Set to false for poison-message tests where failures should exhaust
    ///     attempts and cause inline dead-lettering.
    /// </summary>
    public void SetSimulatedFailuresTransient(bool transient) =>
        _simulatedFailuresAreTransient = transient;

    public void Reset()
    {
        _failing = false;
        _failEveryN = 0;
        _failPredicate = null;
        _simulatedFailuresAreTransient = true;
        Interlocked.Exchange(ref _callCount, 0);
    }

    public bool IsTransient(Exception exception)
    {
        // Our simulated InvalidOperationExceptions are classified per the test's setup.
        // For anything else, fall back to the safe default (non-transient).
        return exception is InvalidOperationException && _simulatedFailuresAreTransient;
    }

    public async Task SendAsync(
        string topicName, string partitionKey,
        IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _callCount);

        if (_failing)
            throw new InvalidOperationException("Simulated broker failure");

        var pred = _failPredicate;

        if (pred != null && messages.Any(pred))
            throw new InvalidOperationException("Simulated failure for matching message");

        var n = _failEveryN;

        if (n > 0 && count % n != 0)
            throw new InvalidOperationException($"Simulated intermittent failure (call {count}, succeeds every {n})");

        // Real send via Kafka producer
        foreach (var msg in messages)
        {
            await _producer.ProduceAsync(topicName,
                new Message<string, byte[]>
                {
                    Key = partitionKey,
                    Value = msg.Payload,
                    Headers = new Headers
                    {
                        { "EventType", System.Text.Encoding.UTF8.GetBytes(msg.EventType) }
                    }
                },
                cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        // Don't dispose the producer — it's owned by the DI container.
        // Just flush any remaining messages with a short timeout.
        try { _producer.Flush(TimeSpan.FromSeconds(3)); }
        catch
        {
            /* best effort */
        }

        return ValueTask.CompletedTask;
    }
}
