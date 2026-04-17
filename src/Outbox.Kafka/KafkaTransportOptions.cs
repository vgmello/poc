// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Kafka;

public sealed class KafkaTransportOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string Acks { get; set; } = "All";

    /// <summary>
    ///     Idempotence is a hard requirement for the outbox library — it is what prevents the
    ///     Kafka producer from committing a non-prefix success pattern on partial sends, which
    ///     would corrupt per-partition-key ordering when the library retries the failed subset.
    ///     This property is retained for binding compatibility but is IGNORED; the producer is
    ///     always built with <c>EnableIdempotence = true</c>.
    /// </summary>
    [Obsolete("Idempotence is required for ordering and is always enabled; this property is ignored.")]
    public bool EnableIdempotence { get; set; } = true;

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
