using Outbox.Core.Options;

namespace Outbox.PerformanceTests.Helpers;

public static class PerfTestOptions
{
    public static readonly Action<OutboxPublisherOptions> Default = o =>
    {
        o.BatchSize = 500;
        o.MaxPublishAttempts = 5;
        o.MinPollIntervalMs = 50;
        o.MaxPollIntervalMs = 1000;
        o.HeartbeatIntervalMs = 5_000;
        o.HeartbeatTimeoutSeconds = 30;
        o.PartitionGracePeriodSeconds = 10;
        o.RebalanceIntervalMs = 5_000;
        o.OrphanSweepIntervalMs = 5_000;
        o.CircuitBreakerFailureThreshold = 3;
        o.CircuitBreakerOpenDurationSeconds = 30;
        o.PublishThreadCount = 4;
    };
}
