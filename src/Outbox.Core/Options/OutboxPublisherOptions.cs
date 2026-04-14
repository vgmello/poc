// Copyright (c) OrgName. All rights reserved.

using System.ComponentModel.DataAnnotations;

namespace Outbox.Core.Options;

public sealed class OutboxPublisherOptions : IValidatableObject
{
    [Required]
    public string PublisherName { get; set; } = "outbox-publisher";

    public string? GroupName { get; set; }

    [Range(1, int.MaxValue)]
    public int BatchSize { get; set; } = 100;

    /// <summary>
    ///     Maximum number of times a (topic, partitionKey) group will be sent before
    ///     the failed messages are dead-lettered. Counts total attempts including the
    ///     first send. Only non-transient failures consume an attempt; transient
    ///     failures (broker unreachable, timeouts, etc.) do not.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxPublishAttempts { get; set; } = 5;

    /// <summary>
    ///     Initial backoff (ms) between in-batch retry attempts.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RetryBackoffBaseMs { get; set; } = 100;

    /// <summary>
    ///     Maximum backoff (ms) between in-batch retry attempts. The actual delay is
    ///     <c>min(RetryBackoffBaseMs * 2^(attempt-1), RetryBackoffMaxMs)</c>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RetryBackoffMaxMs { get; set; } = 2000;

    [Range(1, int.MaxValue)]
    public int MinPollIntervalMs { get; set; } = 100;

    [Range(1, int.MaxValue)]
    public int MaxPollIntervalMs { get; set; } = 5000;

    [Range(1, int.MaxValue)]
    public int HeartbeatIntervalMs { get; set; } = 10_000;

    [Range(1, int.MaxValue)]
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    [Range(1, int.MaxValue)]
    public int PartitionGracePeriodSeconds { get; set; } = 60;

    [Range(1, int.MaxValue)]
    public int RebalanceIntervalMs { get; set; } = 30_000;

    [Range(1, int.MaxValue)]
    public int OrphanSweepIntervalMs { get; set; } = 60_000;

    [Range(1, int.MaxValue)]
    public int CircuitBreakerFailureThreshold { get; set; } = 3;

    [Range(1, int.MaxValue)]
    public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;

    [Range(1, int.MaxValue)]
    public int PublishThreadCount { get; set; } = 4;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxPollIntervalMs < MinPollIntervalMs)
        {
            yield return new ValidationResult(
                "MaxPollIntervalMs must be >= MinPollIntervalMs.",
                new[] { nameof(MaxPollIntervalMs), nameof(MinPollIntervalMs) });
        }

        var heartbeatTimeoutMs = HeartbeatTimeoutSeconds * 1000;

        if (HeartbeatIntervalMs > 0 && heartbeatTimeoutMs > 0 &&
            heartbeatTimeoutMs < HeartbeatIntervalMs * 3)
        {
            yield return new ValidationResult(
                $"HeartbeatTimeoutSeconds ({HeartbeatTimeoutSeconds}s) should be >= " +
                $"3x HeartbeatIntervalMs ({HeartbeatIntervalMs}ms) to tolerate at least 2 missed heartbeats. " +
                "Current timeout fires after only " +
                $"{(double)heartbeatTimeoutMs / HeartbeatIntervalMs:F1}x intervals, " +
                "which may cause false staleness detection and unnecessary rebalancing.",
                new[] { nameof(HeartbeatTimeoutSeconds), nameof(HeartbeatIntervalMs) });
        }

        if (RetryBackoffMaxMs < RetryBackoffBaseMs)
        {
            yield return new ValidationResult(
                "RetryBackoffMaxMs must be >= RetryBackoffBaseMs.",
                new[] { nameof(RetryBackoffMaxMs), nameof(RetryBackoffBaseMs) });
        }
    }
}
