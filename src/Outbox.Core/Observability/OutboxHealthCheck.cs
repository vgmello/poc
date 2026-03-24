// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Outbox.Core.Options;

namespace Outbox.Core.Observability;

/// <summary>
///     Health check that reports the outbox publisher's operational state.
///     Does NOT check DB or broker connectivity — those are handled by Aspire.
/// </summary>
public sealed class OutboxHealthCheck : IHealthCheck
{
    private readonly OutboxHealthState _state;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;

    public OutboxHealthCheck(OutboxHealthState state, IOptionsMonitor<OutboxPublisherOptions> options)
    {
        _state = state;
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        var now = DateTimeOffset.UtcNow;
        var data = new Dictionary<string, object>
        {
            ["publishLoopRunning"] = _state.IsPublishLoopRunning,
            ["lastHeartbeatUtc"] = _state.LastHeartbeatUtc,
            ["lastPollUtc"] = _state.LastPollUtc,
            ["lastSuccessfulPublishUtc"] = _state.LastSuccessfulPublishUtc,
            ["consecutiveLoopRestarts"] = _state.ConsecutiveLoopRestarts
        };

        var openCircuits = _state.GetOpenCircuits();
        if (openCircuits.Count > 0)
            data["openCircuitBreakers"] = string.Join(", ", openCircuits);

        // Unhealthy: publish loop not running
        if (!_state.IsPublishLoopRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Outbox publish loop is not running.", data: data));
        }

        // Unhealthy: heartbeat stale (3x interval = missed 2 consecutive heartbeats)
        var heartbeatStalenessThreshold = TimeSpan.FromMilliseconds(opts.HeartbeatIntervalMs * 3);

        if (_state.LastHeartbeatUtc != DateTimeOffset.MinValue &&
            now - _state.LastHeartbeatUtc > heartbeatStalenessThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Outbox heartbeat is stale. Last heartbeat: {_state.LastHeartbeatUtc:O}.",
                data: data));
        }

        // Unhealthy: publish loop running but no heartbeat has ever succeeded (DB unreachable from startup)
        if (_state.LastHeartbeatUtc == DateTimeOffset.MinValue &&
            _state.PublishLoopStartedAtUtc != DateTimeOffset.MinValue &&
            now - _state.PublishLoopStartedAtUtc > heartbeatStalenessThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Outbox publisher has never completed a heartbeat since startup.",
                data: data));
        }

        // Unhealthy: no polls at all for an extended period (3x max poll interval)
        var pollStalenessThreshold = TimeSpan.FromMilliseconds(opts.MaxPollIntervalMs * 3);

        if (_state.LastPollUtc != DateTimeOffset.MinValue &&
            now - _state.LastPollUtc > pollStalenessThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Outbox publish loop has not polled recently. Last poll: {_state.LastPollUtc:O}.",
                data: data));
        }

        // Unhealthy: publish loop running but no poll has ever succeeded
        if (_state.LastPollUtc == DateTimeOffset.MinValue &&
            _state.PublishLoopStartedAtUtc != DateTimeOffset.MinValue &&
            now - _state.PublishLoopStartedAtUtc > pollStalenessThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Outbox publisher has never completed a poll since startup.",
                data: data));
        }

        // Degraded: circuit breakers open (broker issues for specific topics)
        if (openCircuits.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Circuit breakers open for topics: {string.Join(", ", openCircuits)}.",
                data: data));
        }

        // Degraded: excessive loop restarts (transient issues causing restarts)
        if (_state.ConsecutiveLoopRestarts > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Outbox loops have restarted {_state.ConsecutiveLoopRestarts} time(s).",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Outbox publisher is operating normally.", data: data));
    }
}
