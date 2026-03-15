using Microsoft.Extensions.Options;

namespace Outbox.Core.Options;

internal sealed class OutboxPublisherOptionsValidator : IValidateOptions<OutboxPublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxPublisherOptions options)
    {
        var errors = new List<string>();

        if (options.BatchSize <= 0)
            errors.Add("BatchSize must be greater than 0.");
        if (options.LeaseDurationSeconds <= 0)
            errors.Add("LeaseDurationSeconds must be greater than 0.");
        if (options.MaxRetryCount <= 0)
            errors.Add("MaxRetryCount must be greater than 0.");
        if (options.MinPollIntervalMs <= 0)
            errors.Add("MinPollIntervalMs must be greater than 0.");
        if (options.MaxPollIntervalMs <= 0)
            errors.Add("MaxPollIntervalMs must be greater than 0.");
        if (options.MaxPollIntervalMs < options.MinPollIntervalMs)
            errors.Add("MaxPollIntervalMs must be >= MinPollIntervalMs.");
        if (options.HeartbeatIntervalMs <= 0)
            errors.Add("HeartbeatIntervalMs must be greater than 0.");
        if (options.HeartbeatTimeoutSeconds <= 0)
            errors.Add("HeartbeatTimeoutSeconds must be greater than 0.");
        if (options.PartitionGracePeriodSeconds <= 0)
            errors.Add("PartitionGracePeriodSeconds must be greater than 0.");
        if (options.RebalanceIntervalMs <= 0)
            errors.Add("RebalanceIntervalMs must be greater than 0.");
        if (options.OrphanSweepIntervalMs <= 0)
            errors.Add("OrphanSweepIntervalMs must be greater than 0.");
        if (options.DeadLetterSweepIntervalMs <= 0)
            errors.Add("DeadLetterSweepIntervalMs must be greater than 0.");
        if (options.CircuitBreakerFailureThreshold <= 0)
            errors.Add("CircuitBreakerFailureThreshold must be greater than 0.");
        if (options.CircuitBreakerOpenDurationSeconds <= 0)
            errors.Add("CircuitBreakerOpenDurationSeconds must be greater than 0.");

        // Cross-field invariants — these prevent subtle race conditions and ordering violations.

        if (options.PartitionGracePeriodSeconds > 0 && options.LeaseDurationSeconds > 0 &&
            options.PartitionGracePeriodSeconds < options.LeaseDurationSeconds)
        {
            errors.Add(
                $"PartitionGracePeriodSeconds ({options.PartitionGracePeriodSeconds}) must be >= " +
                $"LeaseDurationSeconds ({options.LeaseDurationSeconds}). " +
                "Otherwise, a new publisher can claim a partition while the old publisher still holds active leases, " +
                "breaking ordering guarantees.");
        }

        var heartbeatTimeoutMs = options.HeartbeatTimeoutSeconds * 1000;
        if (options.HeartbeatIntervalMs > 0 && heartbeatTimeoutMs > 0 &&
            heartbeatTimeoutMs < options.HeartbeatIntervalMs * 3)
        {
            errors.Add(
                $"HeartbeatTimeoutSeconds ({options.HeartbeatTimeoutSeconds}s) should be >= " +
                $"3x HeartbeatIntervalMs ({options.HeartbeatIntervalMs}ms) to tolerate at least 2 missed heartbeats. " +
                "Current timeout fires after only " +
                $"{(double)heartbeatTimeoutMs / options.HeartbeatIntervalMs:F1}x intervals, " +
                "which may cause false staleness detection and unnecessary rebalancing.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
