// Copyright (c) OrgName. All rights reserved.

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
        var (stateChanged, state) = cb.RecordSuccess("orders");
        Assert.False(stateChanged);
        Assert.Equal(CircuitState.Closed, state);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void IsOpen_AfterOpenDurationExpires_ReturnsHalfOpen()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 1, openDurationSeconds: 0);
        var (opened, state) = cb.RecordFailure("orders");
        Assert.True(opened);
        Assert.Equal(CircuitState.Open, state);

        // With openDurationSeconds=0, circuit transitions to HalfOpen on the next check
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

    [Fact]
    public void RecordSuccess_FromHalfOpen_ClosesCircuit()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 0);
        cb.RecordFailure("orders");
        cb.RecordFailure("orders");
        var (opened, state) = cb.RecordFailure("orders");
        Assert.True(opened);
        Assert.Equal(CircuitState.Open, state);

        // With openDurationSeconds=0, GetState transitions to HalfOpen
        Assert.Equal(CircuitState.HalfOpen, cb.GetState("orders"));

        var (closedChanged, closedState) = cb.RecordSuccess("orders");
        Assert.True(closedChanged);
        Assert.Equal(CircuitState.Closed, closedState);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void RecordFailure_InHalfOpen_ReopensCircuit()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 0);
        cb.RecordFailure("orders");
        cb.RecordFailure("orders");
        var (opened, state) = cb.RecordFailure("orders");
        Assert.True(opened);
        Assert.Equal(CircuitState.Open, state);

        // With openDurationSeconds=0, transitions to HalfOpen on state check
        Assert.Equal(CircuitState.HalfOpen, cb.GetState("orders"));

        // Failure in HalfOpen re-opens the circuit
        var (reopened, reopenedState) = cb.RecordFailure("orders");
        Assert.True(reopened);
        Assert.Equal(CircuitState.Open, reopenedState);
    }
}
