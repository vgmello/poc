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

    [Range(1, int.MaxValue)]
    public int MaxRetryCount { get; set; } = 5;

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
    public int DeadLetterSweepIntervalMs { get; set; } = 60_000;

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

        if (MaxRetryCount <= CircuitBreakerFailureThreshold)
        {
            yield return new ValidationResult(
                $"MaxRetryCount ({MaxRetryCount}) should be greater than " +
                $"CircuitBreakerFailureThreshold ({CircuitBreakerFailureThreshold}) " +
                "to allow the circuit breaker to activate before dead-lettering.",
                new[] { nameof(MaxRetryCount), nameof(CircuitBreakerFailureThreshold) });
        }
    }
}
