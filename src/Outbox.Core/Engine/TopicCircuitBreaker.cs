// Copyright (c) OrgName. All rights reserved.

using System.Collections.Concurrent;
using Outbox.Core.Models;

namespace Outbox.Core.Engine;

internal sealed class TopicCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly long _openDurationMs;
    private readonly ConcurrentDictionary<string, CircuitEntry> _circuits = new();

    public TopicCircuitBreaker(int failureThreshold, int openDurationSeconds)
    {
        _failureThreshold = failureThreshold;
        _openDurationMs = openDurationSeconds * 1000L;
    }

    public bool IsOpen(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return false;

        lock (entry.Lock)
        {
            if (entry.State == CircuitState.Open &&
                Environment.TickCount64 - entry.OpenedAtTicks >= _openDurationMs)
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
                Environment.TickCount64 - entry.OpenedAtTicks >= _openDurationMs)
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

            // Any failure in HalfOpen immediately re-opens the circuit (standard pattern).
            // Otherwise, open once the failure count reaches the threshold.
            var shouldOpen = entry.State == CircuitState.HalfOpen ||
                             (entry.FailureCount >= _failureThreshold && entry.State != CircuitState.Open);

            if (shouldOpen)
            {
                entry.State = CircuitState.Open;
                entry.OpenedAtTicks = Environment.TickCount64;

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
        public long OpenedAtTicks;
    }
}
