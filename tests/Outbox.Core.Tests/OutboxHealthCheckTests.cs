// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Observability;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.Core.Tests;

public class OutboxHealthCheckTests
{
    private static OutboxPublisherOptions SmallIntervalOptions() => new()
    {
        BatchSize = 100,
        MaxRetryCount = 10,
        CircuitBreakerFailureThreshold = 3,
        CircuitBreakerOpenDurationSeconds = 30,

        PartitionGracePeriodSeconds = 60,
        // Very small intervals so stale thresholds (3x) are 30ms — easy to exceed with Task.Delay(50)
        HeartbeatIntervalMs = 10,
        HeartbeatTimeoutSeconds = 30,
        MinPollIntervalMs = 10,
        MaxPollIntervalMs = 10,
        RebalanceIntervalMs = 30_000,
        OrphanSweepIntervalMs = 60_000,
        DeadLetterSweepIntervalMs = 60_000
    };

    private static OutboxHealthCheck CreateHealthCheck(OutboxHealthState state, OutboxPublisherOptions? options = null)
    {
        var opts = options ?? SmallIntervalOptions();
        var monitor = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        monitor.CurrentValue.Returns(opts);
        monitor.Get(Arg.Any<string>()).Returns(opts);

        return new OutboxHealthCheck(state, monitor);
    }

    private static HealthCheckContext CreateContext() =>
        new() { Registration = new HealthCheckRegistration("outbox", _ => null!, null, null) };

    [Fact]
    public async Task LoopNotRunning_ReturnsUnhealthy()
    {
        var state = new OutboxHealthState();
        // IsPublishLoopRunning defaults to false
        var check = CreateHealthCheck(state);

        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not running", result.Description);
    }

    [Fact]
    public async Task HeartbeatStale_ReturnsUnhealthy()
    {
        var state = new OutboxHealthState();
        state.SetPublishLoopRunning(true);
        state.RecordHeartbeat(); // record a heartbeat, then let it go stale

        await Task.Delay(50); // exceed 3x HeartbeatIntervalMs (3*10ms = 30ms)

        var check = CreateHealthCheck(state);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("stale", result.Description);
    }

    [Fact]
    public async Task NoHeartbeatSinceStartup_ReturnsUnhealthy()
    {
        var state = new OutboxHealthState();
        state.SetPublishLoopRunning(true);
        // Do NOT call RecordHeartbeat — simulates DB unreachable from startup
        // LastHeartbeatUtc remains MinValue after SetPublishLoopRunning(true)

        await Task.Delay(50); // exceed 3x HeartbeatIntervalMs

        var check = CreateHealthCheck(state);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("never completed a heartbeat", result.Description);
    }

    [Fact]
    public async Task PollStale_ReturnsUnhealthy()
    {
        var state = new OutboxHealthState();
        state.SetPublishLoopRunning(true);
        state.RecordHeartbeat(); // keep heartbeat fresh
        state.RecordPoll(); // record a poll, then let it go stale

        await Task.Delay(50); // exceed 3x MaxPollIntervalMs (3*10ms = 30ms)

        // Refresh heartbeat so it doesn't trigger the heartbeat-stale check first
        state.RecordHeartbeat();

        var check = CreateHealthCheck(state);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not polled recently", result.Description);
    }

    [Fact]
    public async Task NoPollSinceStartup_ReturnsUnhealthy()
    {
        var state = new OutboxHealthState();
        state.SetPublishLoopRunning(true);
        // Keep heartbeat fresh so heartbeat checks don't fire
        state.RecordHeartbeat();
        // Do NOT call RecordPoll — LastPollUtc remains MinValue

        await Task.Delay(50); // exceed 3x MaxPollIntervalMs

        // Refresh heartbeat so it doesn't trigger heartbeat-stale check
        state.RecordHeartbeat();

        var check = CreateHealthCheck(state);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("never completed a poll", result.Description);
    }

    [Fact]
    public async Task OpenCircuitBreakers_ReturnsDegraded()
    {
        var state = new OutboxHealthState();
        state.SetPublishLoopRunning(true);
        state.RecordHeartbeat();
        state.RecordPoll();
        state.SetCircuitOpen("orders");

        var check = CreateHealthCheck(state);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("orders", result.Description);
        Assert.Contains("Circuit breakers open", result.Description);
    }

    [Fact]
    public async Task LoopRestarts_ReturnsDegraded()
    {
        var state = new OutboxHealthState();
        state.SetPublishLoopRunning(true);
        state.RecordHeartbeat();
        state.RecordPoll();
        state.RecordLoopRestart();

        var check = CreateHealthCheck(state);
        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("restarted", result.Description);
    }

    [Fact]
    public async Task AllHealthy_ReturnsHealthy()
    {
        var state = new OutboxHealthState();
        // Use large intervals so the staleness thresholds (3x) are 30 seconds —
        // the check will run well before timestamps go stale.
        var opts = new OutboxPublisherOptions
        {
            BatchSize = 100,
            MaxRetryCount = 10,
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerOpenDurationSeconds = 30,
    
            PartitionGracePeriodSeconds = 60,
            HeartbeatIntervalMs = 10_000,
            HeartbeatTimeoutSeconds = 60,
            MinPollIntervalMs = 100,
            MaxPollIntervalMs = 10_000,
            RebalanceIntervalMs = 30_000,
            OrphanSweepIntervalMs = 60_000,
            DeadLetterSweepIntervalMs = 60_000
        };
        var check = CreateHealthCheck(state, opts);

        state.SetPublishLoopRunning(true);
        state.RecordHeartbeat();
        state.RecordPoll();

        var result = await check.CheckHealthAsync(CreateContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("operating normally", result.Description);
    }
}
