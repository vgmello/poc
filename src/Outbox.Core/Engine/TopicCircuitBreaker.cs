using System.Collections.Concurrent;
using Outbox.Core.Models;

namespace Outbox.Core.Engine;

internal sealed class TopicCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly int _openDurationSeconds;
    private readonly ConcurrentDictionary<string, CircuitEntry> _circuits = new();

    public TopicCircuitBreaker(int failureThreshold, int openDurationSeconds)
    {
        _failureThreshold = failureThreshold;
        _openDurationSeconds = openDurationSeconds;
    }

    public bool IsOpen(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return false;

        lock (entry.Lock)
        {
            if (entry.State == CircuitState.Open &&
                DateTimeOffset.UtcNow > entry.OpenedAtUtc.AddSeconds(_openDurationSeconds).AddMilliseconds(1))
            {
                entry.State = CircuitState.HalfOpen;
                return false;
            }

            return entry.State == CircuitState.Open;
        }
    }

    public CircuitState GetState(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return CircuitState.Closed;

        lock (entry.Lock)
        {
            if (entry.State == CircuitState.Open &&
                DateTimeOffset.UtcNow > entry.OpenedAtUtc.AddSeconds(_openDurationSeconds).AddMilliseconds(1))
            {
                entry.State = CircuitState.HalfOpen;
            }

            return entry.State;
        }
    }

    /// <returns>(stateChanged, newState)</returns>
    public (bool StateChanged, CircuitState NewState) RecordFailure(string topicName)
    {
        var entry = _circuits.GetOrAdd(topicName, _ => new CircuitEntry());

        lock (entry.Lock)
        {
            entry.FailureCount++;
            if (entry.FailureCount >= _failureThreshold && entry.State != CircuitState.Open)
            {
                entry.State = CircuitState.Open;
                entry.OpenedAtUtc = DateTimeOffset.UtcNow;
                return (true, CircuitState.Open);
            }

            return (false, entry.State);
        }
    }

    /// <returns>(stateChanged, newState)</returns>
    public (bool StateChanged, CircuitState NewState) RecordSuccess(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return (false, CircuitState.Closed);

        lock (entry.Lock)
        {
            var previousState = entry.State;
            var previousFailureCount = entry.FailureCount;
            entry.FailureCount = 0;
            entry.State = CircuitState.Closed;

            return (previousState != CircuitState.Closed, CircuitState.Closed);
        }
    }

    private sealed class CircuitEntry
    {
        public readonly object Lock = new();
        public int FailureCount;
        public CircuitState State = CircuitState.Closed;
        public DateTimeOffset OpenedAtUtc;
    }
}
