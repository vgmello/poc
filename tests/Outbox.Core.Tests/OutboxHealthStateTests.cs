// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Observability;
using Xunit;

namespace Outbox.Core.Tests;

public class OutboxHealthStateTests
{
    [Fact]
    public void RecordHeartbeat_UpdatesLastHeartbeatUtc()
    {
        var state = new OutboxHealthState();
        Assert.Equal(DateTimeOffset.MinValue, state.LastHeartbeatUtc);

        state.RecordHeartbeat();

        Assert.NotEqual(DateTimeOffset.MinValue, state.LastHeartbeatUtc);
        Assert.True(DateTimeOffset.UtcNow - state.LastHeartbeatUtc < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RecordSuccessfulPublish_UpdatesLastSuccessfulPublishUtc()
    {
        var state = new OutboxHealthState();
        Assert.Equal(DateTimeOffset.MinValue, state.LastSuccessfulPublishUtc);

        state.RecordSuccessfulPublish();

        Assert.NotEqual(DateTimeOffset.MinValue, state.LastSuccessfulPublishUtc);
        Assert.True(DateTimeOffset.UtcNow - state.LastSuccessfulPublishUtc < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RecordPoll_UpdatesLastPollUtc()
    {
        var state = new OutboxHealthState();
        Assert.Equal(DateTimeOffset.MinValue, state.LastPollUtc);

        state.RecordPoll();

        Assert.NotEqual(DateTimeOffset.MinValue, state.LastPollUtc);
        Assert.True(DateTimeOffset.UtcNow - state.LastPollUtc < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SetPublishLoopRunning_True_SetsRunningAndResetsTimestamps()
    {
        var state = new OutboxHealthState();

        // Record some values first
        state.RecordHeartbeat();
        state.RecordPoll();
        state.RecordSuccessfulPublish();

        Assert.NotEqual(DateTimeOffset.MinValue, state.LastHeartbeatUtc);
        Assert.NotEqual(DateTimeOffset.MinValue, state.LastPollUtc);
        Assert.NotEqual(DateTimeOffset.MinValue, state.LastSuccessfulPublishUtc);

        state.SetPublishLoopRunning(true);

        Assert.True(state.IsPublishLoopRunning);
        Assert.NotEqual(DateTimeOffset.MinValue, state.PublishLoopStartedAtUtc);
        Assert.Equal(DateTimeOffset.MinValue, state.LastHeartbeatUtc);
        Assert.Equal(DateTimeOffset.MinValue, state.LastPollUtc);
        Assert.Equal(DateTimeOffset.MinValue, state.LastSuccessfulPublishUtc);
    }

    [Fact]
    public void SetPublishLoopRunning_False_ClearsFlagWithoutResettingTimestamps()
    {
        var state = new OutboxHealthState();
        state.SetPublishLoopRunning(true);
        state.RecordHeartbeat();
        state.RecordPoll();

        var heartbeat = state.LastHeartbeatUtc;
        var poll = state.LastPollUtc;

        state.SetPublishLoopRunning(false);

        Assert.False(state.IsPublishLoopRunning);
        // Timestamps should NOT be reset when stopping
        Assert.Equal(heartbeat, state.LastHeartbeatUtc);
        Assert.Equal(poll, state.LastPollUtc);
    }

    [Fact]
    public void RecordLoopRestart_IncrementsConsecutiveLoopRestarts()
    {
        var state = new OutboxHealthState();
        Assert.Equal(0, state.ConsecutiveLoopRestarts);

        state.RecordLoopRestart();
        Assert.Equal(1, state.ConsecutiveLoopRestarts);

        state.RecordLoopRestart();
        Assert.Equal(2, state.ConsecutiveLoopRestarts);
    }

    [Fact]
    public void ResetLoopRestarts_SetsCountToZero()
    {
        var state = new OutboxHealthState();
        state.RecordLoopRestart();
        state.RecordLoopRestart();
        Assert.Equal(2, state.ConsecutiveLoopRestarts);

        state.ResetLoopRestarts();

        Assert.Equal(0, state.ConsecutiveLoopRestarts);
    }

    [Fact]
    public void SetCircuitOpen_SetCircuitClosed_TracksOpenCircuits()
    {
        var state = new OutboxHealthState();

        state.SetCircuitOpen("orders");
        state.SetCircuitOpen("shipments");

        var open = state.GetOpenCircuits();
        Assert.Contains("orders", open);
        Assert.Contains("shipments", open);

        state.SetCircuitClosed("orders");

        open = state.GetOpenCircuits();
        Assert.DoesNotContain("orders", open);
        Assert.Contains("shipments", open);
    }

    [Fact]
    public void GetOpenCircuits_ReturnsSnapshotCopy()
    {
        var state = new OutboxHealthState();
        state.SetCircuitOpen("orders");

        var snapshot = state.GetOpenCircuits();
        state.SetCircuitClosed("orders");

        // The snapshot should still contain "orders" — it's a copy, not a live reference
        Assert.Contains("orders", snapshot);
        Assert.Empty(state.GetOpenCircuits());
    }

    [Fact]
    public void InitialState_AllTimestampPropertiesReturnMinValue()
    {
        var state = new OutboxHealthState();

        Assert.Equal(DateTimeOffset.MinValue, state.LastHeartbeatUtc);
        Assert.Equal(DateTimeOffset.MinValue, state.LastSuccessfulPublishUtc);
        Assert.Equal(DateTimeOffset.MinValue, state.LastPollUtc);
        Assert.Equal(DateTimeOffset.MinValue, state.PublishLoopStartedAtUtc);
    }
}
