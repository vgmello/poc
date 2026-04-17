// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Kafka;

public sealed class KafkaTransportOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string Acks { get; set; } = "All";
    public int MessageSendMaxRetries { get; set; } = 3;
    public int RetryBackoffMs { get; set; } = 500;
    public int LingerMs { get; set; } = 5;
    public int MessageTimeoutMs { get; set; } = 15_000;
    public int SendTimeoutSeconds { get; set; } = 15;

    /// <summary>
    ///     Maximum size in bytes for a single sub-batch of messages sent to Kafka.
    ///     Messages are split into sub-batches that stay under this limit.
    ///     Default is 1 MB (1,048,576 bytes), matching Kafka's default max.request.size.
    /// </summary>
    public int MaxBatchSizeBytes { get; set; } = 1_048_576;
}
