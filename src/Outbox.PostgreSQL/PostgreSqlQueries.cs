// Copyright (c) OrgName. All rights reserved.

namespace Outbox.PostgreSQL;

internal sealed class PostgreSqlQueries
{
    // Store queries
    public string RegisterPublisher { get; }
    public string UnregisterPublisher { get; }
    public string FetchBatch { get; }
    public string DeletePublished { get; }
    public string IncrementRetryCount { get; }
    public string DeadLetter { get; }
    public string Heartbeat { get; }
    public string GetTotalPartitions { get; }
    public string GetOwnedPartitions { get; }
    public string RebalanceMarkStale { get; }
    public string RebalanceClaim { get; }
    public string RebalanceRelease { get; }
    public string ClaimOrphanPartitions { get; }
    public string SweepDeadLetters { get; }
    public string GetPendingCount { get; }

    // Dead-letter manager queries
    public string DeadLetterGet { get; }
    public string DeadLetterReplay { get; }
    public string DeadLetterPurge { get; }
    public string DeadLetterPurgeAll { get; }

    public PostgreSqlQueries(string schemaName, string tablePrefix, string sharedSchemaName, string outboxTableName)
    {
        // Data tables — per group, prefixed
        var outboxTable = $"{schemaName}.{tablePrefix}outbox";
        var deadLetterTable = $"{schemaName}.{tablePrefix}outbox_dead_letter";

        // Infrastructure tables — shared, never prefixed
        var publishersTable = $"{sharedSchemaName}.outbox_publishers";
        var partitionsTable = $"{sharedSchemaName}.outbox_partitions";

        // ---- Store queries ----

        RegisterPublisher = $@"
INSERT INTO {publishersTable} (publisher_id, outbox_table_name, registered_at_utc, last_heartbeat_utc, host_name)
VALUES (@publisher_id, @outbox_table_name, clock_timestamp(), clock_timestamp(), @host_name)
ON CONFLICT (outbox_table_name, publisher_id) DO UPDATE
SET last_heartbeat_utc = clock_timestamp(),
    host_name          = EXCLUDED.host_name;";

        UnregisterPublisher = $@"
UPDATE {partitionsTable}
SET    owner_publisher_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
WHERE  owner_publisher_id = @publisher_id
  AND  outbox_table_name = @outbox_table_name;

DELETE FROM {publishersTable}
WHERE  publisher_id = @publisher_id
  AND  outbox_table_name = @outbox_table_name;";

        FetchBatch = $@"
SELECT o.sequence_number, o.topic_name, o.partition_key, o.event_type,
       o.headers, o.payload, o.payload_content_type,
       o.event_datetime_utc, o.event_ordinal,
       o.retry_count, o.created_at_utc
FROM {outboxTable} o
INNER JOIN {partitionsTable} op
    ON  op.outbox_table_name = @outbox_table_name
    AND op.owner_publisher_id = @publisher_id
    AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < clock_timestamp())
    AND ((hashtext(o.partition_key) & 2147483647) % @total_partitions) = op.partition_id
WHERE o.retry_count < @max_retry_count
  AND o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
ORDER BY o.event_datetime_utc, o.event_ordinal, o.sequence_number
LIMIT @batch_size;";

        DeletePublished = $@"
DELETE FROM {outboxTable}
WHERE  sequence_number = ANY(@ids);";

        IncrementRetryCount = $@"
UPDATE {outboxTable}
SET    retry_count = retry_count + 1
WHERE  sequence_number = ANY(@ids);";

        DeadLetter = $@"
WITH dead AS (
    DELETE FROM {outboxTable}
    WHERE  sequence_number = ANY(@ids)
    RETURNING sequence_number, topic_name, partition_key, event_type,
              headers, payload, payload_content_type,
              created_at_utc, retry_count,
              event_datetime_utc, event_ordinal
)
INSERT INTO {deadLetterTable}
    (sequence_number, topic_name, partition_key, event_type, headers, payload,
     payload_content_type,
     created_at_utc, retry_count, event_datetime_utc, event_ordinal,
     dead_lettered_at_utc, last_error)
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       payload_content_type,
       created_at_utc, retry_count, event_datetime_utc, event_ordinal,
       clock_timestamp(), @last_error
FROM dead;";

        Heartbeat = $@"
UPDATE {publishersTable}
SET    last_heartbeat_utc = clock_timestamp()
WHERE  publisher_id = @publisher_id
  AND  outbox_table_name = @outbox_table_name;

UPDATE {partitionsTable}
SET    grace_expires_utc = NULL
WHERE  owner_publisher_id = @publisher_id
  AND  outbox_table_name = @outbox_table_name
  AND  grace_expires_utc IS NOT NULL;";

        GetTotalPartitions = $"SELECT COUNT(*) FROM {partitionsTable} WHERE outbox_table_name = @outbox_table_name;";

        GetOwnedPartitions = $@"
SELECT partition_id
FROM   {partitionsTable}
WHERE  owner_publisher_id = @publisher_id
  AND  outbox_table_name = @outbox_table_name;";

        RebalanceMarkStale = $@"
UPDATE {partitionsTable}
SET    grace_expires_utc = clock_timestamp() + make_interval(secs => @partition_grace_period_seconds)
WHERE  owner_publisher_id <> @publisher_id
  AND  owner_publisher_id IS NOT NULL
  AND  grace_expires_utc IS NULL
  AND  outbox_table_name = @outbox_table_name
  AND  owner_publisher_id NOT IN (
           SELECT publisher_id
           FROM   {publishersTable}
           WHERE  outbox_table_name = @outbox_table_name
             AND  last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)
       );";

        RebalanceClaim = $@"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM {partitionsTable} WHERE outbox_table_name = @outbox_table_name) AS total_partitions,
        (SELECT COUNT(*) FROM {publishersTable}
         WHERE outbox_table_name = @outbox_table_name
           AND last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_publishers,
        (SELECT COUNT(*) FROM {partitionsTable}
         WHERE outbox_table_name = @outbox_table_name
           AND owner_publisher_id = @publisher_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_publishers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_claim AS (
    SELECT op.partition_id
    FROM {partitionsTable} op, fair f
    WHERE op.outbox_table_name = @outbox_table_name
      AND (op.owner_publisher_id IS NULL OR op.grace_expires_utc < clock_timestamp())
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE {partitionsTable}
SET    owner_publisher_id = @publisher_id,
       owned_since_utc   = clock_timestamp(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  {partitionsTable}.partition_id = to_claim.partition_id
  AND  {partitionsTable}.outbox_table_name = @outbox_table_name;";

        RebalanceRelease = $@"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM {partitionsTable} WHERE outbox_table_name = @outbox_table_name) AS total_partitions,
        (SELECT COUNT(*) FROM {publishersTable}
         WHERE outbox_table_name = @outbox_table_name
           AND last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_publishers,
        (SELECT COUNT(*) FROM {partitionsTable}
         WHERE outbox_table_name = @outbox_table_name
           AND owner_publisher_id = @publisher_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_publishers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_release AS (
    SELECT op.partition_id
    FROM {partitionsTable} op, fair f
    WHERE op.outbox_table_name = @outbox_table_name
      AND op.owner_publisher_id = @publisher_id
      AND f.currently_owned > f.fair_share
    ORDER BY op.partition_id DESC
    LIMIT GREATEST(0, (SELECT f.currently_owned - f.fair_share FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE {partitionsTable}
SET    owner_publisher_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
FROM   to_release
WHERE  {partitionsTable}.partition_id = to_release.partition_id
  AND  {partitionsTable}.outbox_table_name = @outbox_table_name;";

        ClaimOrphanPartitions = $@"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM {partitionsTable} WHERE outbox_table_name = @outbox_table_name) AS total_partitions,
        (SELECT COUNT(*) FROM {publishersTable}
         WHERE outbox_table_name = @outbox_table_name
           AND last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_publishers,
        (SELECT COUNT(*) FROM {partitionsTable}
         WHERE outbox_table_name = @outbox_table_name
           AND owner_publisher_id = @publisher_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_publishers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_claim AS (
    SELECT op.partition_id
    FROM {partitionsTable} op, fair f
    WHERE op.outbox_table_name = @outbox_table_name
      AND op.owner_publisher_id IS NULL
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE {partitionsTable}
SET    owner_publisher_id = @publisher_id,
       owned_since_utc   = clock_timestamp(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  {partitionsTable}.partition_id = to_claim.partition_id
  AND  {partitionsTable}.outbox_table_name = @outbox_table_name;";

        SweepDeadLetters = $@"
WITH dead AS (
    DELETE FROM {outboxTable} o
    USING (
        SELECT ot.sequence_number
        FROM {outboxTable} ot
        INNER JOIN {partitionsTable} op
            ON  op.outbox_table_name = @outbox_table_name
            AND op.owner_publisher_id = @publisher_id
            AND ((hashtext(ot.partition_key) & 2147483647) % (SELECT COUNT(*) FROM {partitionsTable} WHERE outbox_table_name = @outbox_table_name)) = op.partition_id
        WHERE ot.retry_count >= @max_retry_count
        FOR UPDATE OF ot SKIP LOCKED
    ) d
    WHERE o.sequence_number = d.sequence_number
    RETURNING o.sequence_number, o.topic_name, o.partition_key, o.event_type,
              o.headers, o.payload, o.payload_content_type,
              o.created_at_utc, o.retry_count,
              o.event_datetime_utc, o.event_ordinal
)
INSERT INTO {deadLetterTable}
    (sequence_number, topic_name, partition_key, event_type, headers, payload,
     payload_content_type,
     created_at_utc, retry_count, event_datetime_utc, event_ordinal,
     dead_lettered_at_utc, last_error)
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       payload_content_type,
       created_at_utc, retry_count, event_datetime_utc, event_ordinal,
       clock_timestamp(), @last_error
FROM dead;";

        GetPendingCount = $"SELECT COUNT(*) FROM {outboxTable};";

        // ---- Dead-letter manager queries ----

        DeadLetterGet = $@"
SELECT dead_letter_seq, sequence_number, topic_name, partition_key, event_type, headers, payload,
       payload_content_type,
       event_datetime_utc, event_ordinal, retry_count, created_at_utc,
       dead_lettered_at_utc, last_error
FROM   {deadLetterTable}
ORDER  BY dead_letter_seq
LIMIT  @limit OFFSET @offset;";

        DeadLetterReplay = $@"
WITH replayed AS (
    DELETE FROM {deadLetterTable}
    WHERE  dead_letter_seq = ANY(@ids)
    RETURNING sequence_number, topic_name, partition_key, event_type, headers, payload,
              payload_content_type,
              created_at_utc, event_datetime_utc, event_ordinal
)
INSERT INTO {outboxTable}
    (topic_name, partition_key, event_type, headers, payload,
     payload_content_type,
     created_at_utc, event_datetime_utc, event_ordinal,
     retry_count)
SELECT topic_name, partition_key, event_type, headers, payload,
       payload_content_type,
       created_at_utc, event_datetime_utc, event_ordinal,
       0
FROM replayed;";

        DeadLetterPurge = $@"
DELETE FROM {deadLetterTable}
WHERE  dead_letter_seq = ANY(@ids);";

        DeadLetterPurgeAll = $"DELETE FROM {deadLetterTable};";
    }
}
