namespace Outbox.Publisher;

public sealed class KafkaOutboxPublisherOptions
{
    public int BatchSize { get; set; } = 100;
    public int LeaseDurationSeconds { get; set; } = 45;
    public int MaxRetryCount { get; set; } = 5;
    public int MinPollIntervalMs { get; set; } = 100;
    public int MaxPollIntervalMs { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 10_000;
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int PartitionGracePeriodSeconds { get; set; } = 60;
    public int DeadLetterSweepIntervalMs { get; set; } = 60_000;
    public int OrphanSweepIntervalMs { get; set; } = 60_000;
    public int RebalanceIntervalMs { get; set; } = 30_000;
    public int KafkaProduceTimeoutSeconds { get; set; } = 15;
    public int CircuitBreakerFailureThreshold { get; set; } = 3;
    public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;
    public int SqlCommandTimeoutSeconds { get; set; } = 30;
    public Action<string, Exception?>? OnError { get; set; }
}
