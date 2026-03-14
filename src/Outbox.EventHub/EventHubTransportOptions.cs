namespace Outbox.EventHub;

public sealed class EventHubTransportOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string EventHubName { get; set; } = string.Empty;
    public int MaxBatchSizeBytes { get; set; } = 1_048_576; // 1MB
    public int SendTimeoutSeconds { get; set; } = 15;
}
