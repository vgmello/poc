namespace Outbox.Core.Observability;

/// <summary>
/// Thread-safe container for outbox publisher operational state.
/// Written by <see cref="Engine.OutboxPublisherService"/>, read by health checks.
/// </summary>
public sealed class OutboxHealthState
{
    private long _lastHeartbeatTicks;
    private long _lastSuccessfulPublishTicks;
    private long _lastPollTicks;
    private volatile bool _isPublishLoopRunning;
    private volatile int _consecutiveLoopRestarts;
    private long _publishLoopStartedAtTicks;

    // Circuit breaker state: topic name → true if open
    private readonly object _circuitLock = new();
    private readonly HashSet<string> _openCircuits = new();

    public DateTimeOffset LastHeartbeatUtc => TicksToDateTimeOffset(Volatile.Read(ref _lastHeartbeatTicks));
    public DateTimeOffset LastSuccessfulPublishUtc => TicksToDateTimeOffset(Volatile.Read(ref _lastSuccessfulPublishTicks));
    public DateTimeOffset LastPollUtc => TicksToDateTimeOffset(Volatile.Read(ref _lastPollTicks));
    public bool IsPublishLoopRunning => _isPublishLoopRunning;
    public DateTimeOffset PublishLoopStartedAtUtc => TicksToDateTimeOffset(Volatile.Read(ref _publishLoopStartedAtTicks));
    public int ConsecutiveLoopRestarts => _consecutiveLoopRestarts;

    public void RecordHeartbeat() =>
        Volatile.Write(ref _lastHeartbeatTicks, DateTimeOffset.UtcNow.Ticks);

    public void RecordSuccessfulPublish() =>
        Volatile.Write(ref _lastSuccessfulPublishTicks, DateTimeOffset.UtcNow.Ticks);

    public void RecordPoll() =>
        Volatile.Write(ref _lastPollTicks, DateTimeOffset.UtcNow.Ticks);

    public void SetPublishLoopRunning(bool running)
    {
        _isPublishLoopRunning = running;
        if (running)
            Volatile.Write(ref _publishLoopStartedAtTicks, DateTimeOffset.UtcNow.Ticks);
    }

    public void RecordLoopRestart() =>
        Interlocked.Increment(ref _consecutiveLoopRestarts);

    public void ResetLoopRestarts() =>
        Interlocked.Exchange(ref _consecutiveLoopRestarts, 0);

    public void SetCircuitOpen(string topicName)
    {
        lock (_circuitLock)
            _openCircuits.Add(topicName);
    }

    public void SetCircuitClosed(string topicName)
    {
        lock (_circuitLock)
            _openCircuits.Remove(topicName);
    }

    public IReadOnlySet<string> GetOpenCircuits()
    {
        lock (_circuitLock)
            return new HashSet<string>(_openCircuits);
    }

    private static DateTimeOffset TicksToDateTimeOffset(long ticks) =>
        ticks == 0 ? DateTimeOffset.MinValue : new DateTimeOffset(ticks, TimeSpan.Zero);
}
