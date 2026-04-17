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

    /// <summary>
    ///     Returns <c>true</c> if the caller must treat the circuit as open (skip the send).
    ///     Exactly ONE caller per Open→HalfOpen transition is allowed through as the probe;
    ///     concurrent callers see the half-open state as "busy" and are told to back off.
    /// </summary>
    public bool IsOpen(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return false;

        lock (entry.Lock)
        {
            if (entry.State == CircuitState.Closed)
                return false;

            if (entry.State == CircuitState.Open)
            {
                if (Environment.TickCount64 - entry.OpenedAtTicks < _openDurationMs)
                    return true;

                // Duration elapsed — transition to HalfOpen and hand the probe slot to
                // this caller. Subsequent callers will observe ProbeInFlight and be told
                // the circuit is still effectively open until the probe resolves.
                entry.State = CircuitState.HalfOpen;
                entry.ProbeInFlight = true;
                return false;
            }

            // HalfOpen: block everyone except the single probe in flight. The probe
            // caller does not re-enter IsOpen for the same send, so any caller hitting
            // this branch is a concurrent second worker and must wait.
            return entry.ProbeInFlight;
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
                // Observational only: report the effective state without mutating or
                // burning the probe slot. Only IsOpen grants a probe.
                return CircuitState.HalfOpen;
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

            // Lazily promote a duration-expired Open to HalfOpen so a failure observed
            // in that window is treated as a failed probe (re-opens the circuit).
            if (entry.State == CircuitState.Open &&
                Environment.TickCount64 - entry.OpenedAtTicks >= _openDurationMs)
            {
                entry.State = CircuitState.HalfOpen;
            }

            // Any failure in HalfOpen immediately re-opens the circuit (standard pattern).
            // Otherwise, open once the failure count reaches the threshold.
            var shouldOpen = entry.State == CircuitState.HalfOpen ||
                             (entry.FailureCount >= _failureThreshold && entry.State != CircuitState.Open);

            if (shouldOpen)
            {
                entry.State = CircuitState.Open;
                entry.OpenedAtTicks = Environment.TickCount64;
                entry.ProbeInFlight = false;

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
            entry.ProbeInFlight = false;

            return (previousState != CircuitState.Closed, CircuitState.Closed);
        }
    }

    private sealed class CircuitEntry
    {
        public readonly object Lock = new();
        public int FailureCount;
        public CircuitState State = CircuitState.Closed;
        public long OpenedAtTicks;
        public bool ProbeInFlight;
    }
}
