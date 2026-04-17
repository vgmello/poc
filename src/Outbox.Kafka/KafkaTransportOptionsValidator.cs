// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.Options;
using Outbox.Core.Options;

namespace Outbox.Kafka;

/// <summary>
///     Validates that <see cref="OutboxPublisherOptions.PartitionGracePeriodSeconds"/> leaves
///     enough headroom to cover an in-flight Kafka send plus one heartbeat interval. If a
///     publisher gets stuck inside <c>KafkaOutboxTransport.SendAsync</c> for the full
///     <see cref="KafkaTransportOptions.SendTimeoutSeconds"/> window, another publisher can
///     claim its partitions once the grace period elapses — and the two-writer window on a
///     single partition key is what corrupts per-key ordering. Forcing
///     <c>grace > sendTimeout + heartbeatInterval</c> keeps that window closed in config.
/// </summary>
internal sealed class KafkaTransportOptionsValidator : IValidateOptions<KafkaTransportOptions>
{
    private readonly IOptionsMonitor<OutboxPublisherOptions> _publisherOptions;

    public KafkaTransportOptionsValidator(IOptionsMonitor<OutboxPublisherOptions> publisherOptions)
    {
        _publisherOptions = publisherOptions;
    }

    public ValidateOptionsResult Validate(string? name, KafkaTransportOptions options)
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
                $"KafkaTransportOptions.SendTimeoutSeconds ({options.SendTimeoutSeconds}s) + " +
                $"HeartbeatIntervalMs ({pubOpts.HeartbeatIntervalMs}ms) = " +
                $"{minimumGraceMs / 1000.0:F1}s. A grace period shorter than an in-flight send " +
                "allows a second publisher to claim partitions while the first is still writing " +
                "to them, which corrupts per-partition-key ordering.");
        }

        return ValidateOptionsResult.Success;
    }
}
