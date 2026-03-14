using Outbox.Core.Engine;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests;

public class TopicCircuitBreakerTests
{
    [Fact]
    public void IsOpen_NewCircuit_ReturnsFalse()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void RecordFailure_BelowThreshold_CircuitStaysClosed()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        var (opened, _) = cb.RecordFailure("orders");
        Assert.False(opened);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void RecordFailure_AtThreshold_CircuitOpens()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        cb.RecordFailure("orders");
        cb.RecordFailure("orders");
        var (opened, state) = cb.RecordFailure("orders");
        Assert.True(opened);
        Assert.Equal(CircuitState.Open, state);
        Assert.True(cb.IsOpen("orders"));
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        cb.RecordFailure("orders");
        cb.RecordFailure("orders");
        var (closed, state) = cb.RecordSuccess("orders");
        Assert.True(closed);
        Assert.Equal(CircuitState.Closed, state);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void IsOpen_AfterOpenDurationExpires_ReturnsHalfOpen()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 1, openDurationSeconds: 0);
        cb.RecordFailure("orders");
        Assert.True(cb.IsOpen("orders"));

        Thread.Sleep(50);
        Assert.False(cb.IsOpen("orders"));
        Assert.Equal(CircuitState.HalfOpen, cb.GetState("orders"));
    }

    [Fact]
    public void DifferentTopics_HaveIndependentCircuits()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 1, openDurationSeconds: 30);
        cb.RecordFailure("orders");
        Assert.True(cb.IsOpen("orders"));
        Assert.False(cb.IsOpen("shipments"));
    }
}
