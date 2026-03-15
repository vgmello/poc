using System.Data.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Options;

namespace Outbox.PostgreSQL;

public sealed class PostgreSqlOutboxStore : IOutboxStore
{
    private readonly PostgreSqlDbHelper _db;
    private readonly PostgreSqlStoreOptions _options;
    private readonly OutboxPublisherOptions _publisherOptions;

    public PostgreSqlOutboxStore(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<PostgreSqlStoreOptions> options,
        IOptions<OutboxPublisherOptions> publisherOptions)
    {
        _options = options.Value;
        _publisherOptions = publisherOptions.Value;
        _db = new PostgreSqlDbHelper(connectionFactory, serviceProvider, _options);
    }

    // -------------------------------------------------------------------------
    // Producer lifecycle
    // -------------------------------------------------------------------------

    public async Task<string> RegisterProducerAsync(CancellationToken ct)
    {
        string producerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        string hostName = Environment.MachineName;

        const string sql = @"
INSERT INTO outbox_producers (producer_id, registered_at_utc, last_heartbeat_utc, host_name)
VALUES (@producer_id, clock_timestamp(), clock_timestamp(), @host_name)
ON CONFLICT (producer_id) DO UPDATE
SET last_heartbeat_utc = clock_timestamp(),
    host_name          = EXCLUDED.host_name;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@host_name", hostName);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return producerId;
    }

    public async Task UnregisterProducerAsync(string producerId, CancellationToken ct)
    {
        const string sql = @"
UPDATE outbox_partitions
SET    owner_producer_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
WHERE  owner_producer_id = @producer_id;

DELETE FROM outbox_producers
WHERE  producer_id = @producer_id;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);

            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Lease and publish operations
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        string producerId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct)
    {
        const string sql = @"
WITH batch AS (
    SELECT o.sequence_number
    FROM outbox o
    INNER JOIN outbox_partitions op
        ON  op.owner_producer_id = @publisher_id
        AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < clock_timestamp())
        AND (ABS(hashtext(o.partition_key)) % @total_partitions) = op.partition_id
    WHERE (o.leased_until_utc IS NULL OR o.leased_until_utc < clock_timestamp())
      AND o.retry_count < @max_retry_count
    ORDER BY o.event_datetime_utc, o.event_ordinal
    LIMIT @batch_size
    FOR UPDATE OF o SKIP LOCKED
)
UPDATE outbox o
SET    leased_until_utc = clock_timestamp() + make_interval(secs => @lease_duration_seconds),
       lease_owner      = @publisher_id,
       retry_count      = CASE WHEN o.leased_until_utc IS NOT NULL
                               THEN o.retry_count + 1
                               ELSE o.retry_count END
FROM   batch b
WHERE  o.sequence_number = b.sequence_number
RETURNING o.sequence_number, o.topic_name, o.partition_key, o.event_type,
          o.headers, o.payload, o.event_datetime_utc, o.event_ordinal,
          o.retry_count, o.created_at_utc;";

        int totalPartitions = await GetTotalPartitionsAsync(ct).ConfigureAwait(false);
        if (totalPartitions == 0)
            return Array.Empty<OutboxMessage>();

        var rows = new List<OutboxMessage>();

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            rows.Clear();
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@batch_size", batchSize);
            cmd.Parameters.AddWithValue("@lease_duration_seconds", (double)leaseDurationSeconds);
            cmd.Parameters.AddWithValue("@publisher_id", producerId);
            cmd.Parameters.AddWithValue("@total_partitions", totalPartitions);
            cmd.Parameters.AddWithValue("@max_retry_count", maxRetryCount);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                rows.Add(new OutboxMessage(
                    SequenceNumber: reader.GetInt64(0),
                    TopicName: reader.GetString(1),
                    PartitionKey: reader.GetString(2),
                    EventType: reader.GetString(3),
                    Headers: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Payload: reader.GetString(5),
                    EventDateTimeUtc: reader.GetFieldValue<DateTimeOffset>(6),
                    EventOrdinal: reader.GetInt16(7),
                    RetryCount: reader.GetInt32(8),
                    CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(9)));
            }
        }, ct).ConfigureAwait(false);

        return rows;
    }

    public async Task DeletePublishedAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
DELETE FROM outbox
WHERE  sequence_number = ANY(@published_ids)
  AND  lease_owner = @publisher_id;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@publisher_id", producerId);
            cmd.Parameters.Add(new NpgsqlParameter("@published_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequenceNumbers.ToArray()
            });
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
UPDATE outbox
SET    leased_until_utc = NULL,
       lease_owner      = NULL
WHERE  sequence_number = ANY(@ids)
  AND  lease_owner = @publisher_id;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@publisher_id", producerId);
            cmd.Parameters.Add(new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequenceNumbers.ToArray()
            });
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct)
    {
        const string sql = @"
WITH dead AS (
    DELETE FROM outbox
    WHERE  sequence_number = ANY(@ids)
    RETURNING sequence_number, topic_name, partition_key, event_type,
              headers, payload, created_at_utc, retry_count,
              event_datetime_utc, event_ordinal
)
INSERT INTO outbox_dead_letter
    (sequence_number, topic_name, partition_key, event_type, headers, payload,
     created_at_utc, retry_count, event_datetime_utc, event_ordinal,
     dead_lettered_at_utc, last_error)
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       created_at_utc, retry_count, event_datetime_utc, event_ordinal,
       clock_timestamp(), @last_error
FROM dead;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequenceNumbers.ToArray()
            });
            cmd.Parameters.Add(new NpgsqlParameter("@last_error", NpgsqlDbType.Varchar, 2000)
            {
                Value = (object?)lastError ?? DBNull.Value
            });
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Heartbeat and partition management
    // -------------------------------------------------------------------------

    public async Task HeartbeatAsync(string producerId, CancellationToken ct)
    {
        const string sql = @"
UPDATE outbox_producers
SET    last_heartbeat_utc = clock_timestamp()
WHERE  producer_id = @producer_id;

UPDATE outbox_partitions
SET    grace_expires_utc = NULL
WHERE  owner_producer_id = @producer_id
  AND  grace_expires_utc IS NOT NULL;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> GetTotalPartitionsAsync(CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM outbox_partitions;";

        int result = 0;
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            var scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            result = Convert.ToInt32(scalar);
        }, ct).ConfigureAwait(false);

        return result;
    }

    public async Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string producerId, CancellationToken ct)
    {
        const string sql = @"
SELECT partition_id
FROM   outbox_partitions
WHERE  owner_producer_id = @producer_id;";

        var partitions = new List<int>();
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            partitions.Clear();
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                partitions.Add(reader.GetInt32(0));
        }, ct).ConfigureAwait(false);

        return partitions;
    }

    public async Task RebalanceAsync(string producerId, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(ct).ConfigureAwait(false);

        // Step 1: Mark stale producers' partitions as entering grace period
        const string markStaleSql = @"
UPDATE outbox_partitions
SET    grace_expires_utc = clock_timestamp() + make_interval(secs => @partition_grace_period_seconds)
WHERE  owner_producer_id <> @producer_id
  AND  owner_producer_id IS NOT NULL
  AND  grace_expires_utc IS NULL
  AND  owner_producer_id NOT IN (
           SELECT producer_id
           FROM   outbox_producers
           WHERE  last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)
       );";

        await using (var cmd = _db.CreateCommand(markStaleSql, conn))
        {
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_publisherOptions.HeartbeatTimeoutSeconds);
            cmd.Parameters.AddWithValue("@partition_grace_period_seconds", (double)_publisherOptions.PartitionGracePeriodSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 2: Calculate fair share and claim unowned/grace-expired partitions
        const string claimSql = @"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_claim AS (
    SELECT op.partition_id
    FROM outbox_partitions op, fair f
    WHERE (op.owner_producer_id IS NULL OR op.grace_expires_utc < clock_timestamp())
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE outbox_partitions
SET    owner_producer_id = @producer_id,
       owned_since_utc   = clock_timestamp(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  outbox_partitions.partition_id = to_claim.partition_id;";

        await using (var cmd = _db.CreateCommand(claimSql, conn))
        {
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_publisherOptions.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 3: Release excess above fair share
        const string releaseSql = @"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_release AS (
    SELECT op.partition_id
    FROM outbox_partitions op, fair f
    WHERE op.owner_producer_id = @producer_id
      AND f.currently_owned > f.fair_share
    ORDER BY op.partition_id DESC
    LIMIT GREATEST(0, (SELECT f.currently_owned - f.fair_share FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE outbox_partitions
SET    owner_producer_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
FROM   to_release
WHERE  outbox_partitions.partition_id = to_release.partition_id;";

        await using (var cmd = _db.CreateCommand(releaseSql, conn))
        {
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_publisherOptions.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ClaimOrphanPartitionsAsync(string producerId, CancellationToken ct)
    {
        const string sql = @"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_claim AS (
    SELECT op.partition_id
    FROM outbox_partitions op, fair f
    WHERE op.owner_producer_id IS NULL
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE outbox_partitions
SET    owner_producer_id = @producer_id,
       owned_since_utc   = clock_timestamp(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  outbox_partitions.partition_id = to_claim.partition_id;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_publisherOptions.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct)
    {
        const string sql = @"
WITH dead AS (
    DELETE FROM outbox o
    USING (
        SELECT sequence_number
        FROM outbox
        WHERE retry_count >= @max_retry_count
          AND (leased_until_utc IS NULL OR leased_until_utc < clock_timestamp())
        FOR UPDATE SKIP LOCKED
    ) d
    WHERE o.sequence_number = d.sequence_number
    RETURNING o.sequence_number, o.topic_name, o.partition_key, o.event_type,
              o.headers, o.payload, o.created_at_utc, o.retry_count,
              o.event_datetime_utc, o.event_ordinal
)
INSERT INTO outbox_dead_letter
    (sequence_number, topic_name, partition_key, event_type, headers, payload,
     created_at_utc, retry_count, event_datetime_utc, event_ordinal,
     dead_lettered_at_utc, last_error)
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       created_at_utc, retry_count, event_datetime_utc, event_ordinal,
       clock_timestamp(), NULL
FROM dead;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@max_retry_count", maxRetryCount);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

}
