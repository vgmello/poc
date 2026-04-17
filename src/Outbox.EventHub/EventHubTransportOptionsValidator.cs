// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.Options;
using Outbox.Core.Options;

namespace Outbox.EventHub;

/// <summary>
///     Validates that <see cref="OutboxPublisherOptions.PartitionGracePeriodSeconds"/> leaves
///     enough headroom to cover an in-flight EventHub send plus one heartbeat interval. Same
///     split-brain concern as the Kafka validator: a grace period shorter than an in-flight
///     send allows a second publisher to claim partitions mid-write and corrupt per-key order.
/// </summary>
internal sealed class EventHubTransportOptionsValidator : IValidateOptions<EventHubTransportOptions>
{
    private readonly IOptionsMonitor<OutboxPublisherOptions> _publisherOptions;

    public EventHubTransportOptionsValidator(IOptionsMonitor<OutboxPublisherOptions> publisherOptions)
    {
        _publisherOptions = publisherOptions;
    }

    public ValidateOptionsResult Validate(string? name, EventHubTransportOptions options)
    {
        var publisherName = string.IsNullOrEmpty(name) ? Options.DefaultName : name;
        var pubOpts = _publisherOptions.Get(publisherName);

        var sendTimeoutMs = options.SendTimeoutSeconds * 1000;
        var graceMs = pubOpts.PartitionGracePeriodSeconds * 1000;
        var minimumGraceMs = sendTimeoutMs + pubOpts.HeartbeatIntervalMs;

        if (graceMs <= minimumGraceMs)
        {
            return ValidateOptionsResult.Fail(
                $"PartitionGracePeriodSeconds ({pubOpts.PartitionGracePeriodSeconds}s) must exceed " +
                $"EventHubTransportOptions.SendTimeoutSeconds ({options.SendTimeoutSeconds}s) + " +
                $"HeartbeatIntervalMs ({pubOpts.HeartbeatIntervalMs}ms) = " +
                $"{minimumGraceMs / 1000.0:F1}s. A grace period shorter than an in-flight send " +
                "allows a second publisher to claim partitions while the first is still writing " +
                "to them, which corrupts per-partition-key ordering.");
        }

        return ValidateOptionsResult.Success;
    }
}
