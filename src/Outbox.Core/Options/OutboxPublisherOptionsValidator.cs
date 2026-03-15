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

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
