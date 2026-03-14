using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;
using NpgsqlTypes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

/// <summary>
/// Polls the outbox table, publishes leased rows to Azure EventHub, then deletes them.
/// PostgreSQL implementation using Npgsql.
///
/// Features:
///   - Partition affinity: each publisher owns a subset of EventHub partitions,
///     ensuring per-partition-key ordering (see §4 of EventHubOutboxSpec.PostgreSQL.md).
///   - Dynamic rebalance: fair-share partition assignment adjusts automatically
///     as instances join or leave.
///   - Heartbeat: keeps outbox_producers up-to-date so peers detect crashes;
///     clears grace_expires_utc on owned partitions if the producer recovers.
///   - Unified poll: a single query leases both fresh and expired-lease rows
///     in event_datetime_utc/event_ordinal order, guaranteeing per-partition-key
///     causal ordering even during crash recovery. retry_count is conditionally
///     incremented only for previously-leased (recovered) rows.
///   - Dead-letter sweep: rows exceeding MaxRetryCount are moved to
///     outbox_dead_letter, isolating poison messages.
///   - Adaptive polling: exponential backoff on empty polls; instant reset on
///     the first non-empty poll.
///   - Array-based deletes: uses ANY(@ids::bigint[]) for efficient batch operations
///     (replaces SQL Server's TVP pattern).
///   - EventHub batch limits: splits each topic batch to stay under 1 MB.
///   - Producer lifecycle: clean registration, heartbeat, and graceful shutdown.
///
/// Key differences from the SQL Server implementation:
///   - Uses Npgsql (NpgsqlConnection, NpgsqlCommand) instead of Microsoft.Data.SqlClient
///   - FOR UPDATE SKIP LOCKED replaces ROWLOCK + READPAST hints
///   - UPDATE...RETURNING replaces UPDATE...OUTPUT inserted.*
///   - CTE DELETE...RETURNING + INSERT replaces DELETE...OUTPUT INTO
///   - INSERT...ON CONFLICT replaces MERGE
///   - ANY(@ids::bigint[]) replaces Table-Valued Parameters (TVP)
///   - hashtext() replaces CHECKSUM() for partition bucket assignment
///   - make_interval(secs =>) replaces DATEADD(SECOND, ...)
///   - NOW() replaces SYSUTCDATETIME()
///   - LIMIT replaces TOP
/// </summary>
public sealed class PostgreSqlOutboxPublisher : IAsyncDisposable
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    private readonly string _connectionString;
    private readonly string _eventHubConnectionString;
    private readonly string _producerId;
    private readonly string _hostName;
    private readonly PostgreSqlOutboxPublisherOptions _options;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private readonly Dictionary<string, EventHubProducerClient> _producerClients = new();
    private readonly SemaphoreSlim _producerClientLock = new(1, 1);

    private CancellationTokenSource _cts = new();
    private Task _publishLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private Task _deadLetterSweepLoop = Task.CompletedTask;
    private Task _rebalanceLoop = Task.CompletedTask;
    private Task _orphanSweepLoop = Task.CompletedTask;

    private int _totalPartitionCount = 0;

    // Adaptive backoff state for the publish loop.
    private volatile int _currentPollIntervalMs;

    // Last known publish error, for dead-letter diagnostics.
    private volatile string? _lastPublishError;

    // Circuit breaker: tracks consecutive failures per topic.
    private readonly Dictionary<string, int> _topicFailureCount = new();
    private readonly Dictionary<string, DateTime> _topicCircuitOpenUntil = new();
    private readonly object _circuitLock = new();

    private bool _disposed;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public PostgreSqlOutboxPublisher(
        string connectionString,
        string eventHubConnectionString,
        PostgreSqlOutboxPublisherOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));
        if (string.IsNullOrWhiteSpace(eventHubConnectionString))
            throw new ArgumentNullException(nameof(eventHubConnectionString));

        _connectionString = connectionString;
        _eventHubConnectionString = eventHubConnectionString;
        _options = options ?? new PostgreSqlOutboxPublisherOptions();
        _producerId = $"{Environment.MachineName}:{System.Diagnostics.Process.GetCurrentProcess().Id}:{Guid.NewGuid():N}";
        _hostName = Environment.MachineName;
        _currentPollIntervalMs = _options.MinPollIntervalMs;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers this producer, initialises partition ownership, and starts
    /// all background loops.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_options.PartitionGracePeriodSeconds <= _options.LeaseDurationSeconds)
            throw new InvalidOperationException(
                $"PartitionGracePeriodSeconds ({_options.PartitionGracePeriodSeconds}) must exceed LeaseDurationSeconds ({_options.LeaseDurationSeconds}) to prevent ordering violations during partition handover.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await RegisterProducerAsync(_cts.Token).ConfigureAwait(false);

        _totalPartitionCount = await GetTotalPartitionCountAsync(_cts.Token).ConfigureAwait(false);
        if (_totalPartitionCount == 0)
            throw new InvalidOperationException(
                "outbox_partitions is empty. Run the partition initialisation script (EventHubOutbox.PostgreSQL.sql §5a) before starting publishers.");

        await RebalanceAsync(_cts.Token).ConfigureAwait(false);

        _heartbeatLoop = HeartbeatLoopAsync(_cts.Token);
        _rebalanceLoop = RebalanceLoopAsync(_cts.Token);
        _publishLoop = PublishLoopAsync(_cts.Token);
        _deadLetterSweepLoop = DeadLetterSweepLoopAsync(_cts.Token);
        _orphanSweepLoop = OrphanSweepLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Graceful shutdown: stops polling, waits for in-flight sends, releases
    /// partition ownership, and unregisters the producer.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();

        try
        {
            await Task.WhenAll(
                _publishLoop,
                _heartbeatLoop,
                _deadLetterSweepLoop,
                _rebalanceLoop,
                _orphanSweepLoop).ConfigureAwait(false);
        }
        finally
        {
            await UnregisterProducerAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort on dispose.
        }

        await _producerClientLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var client in _producerClients.Values)
                await client.DisposeAsync().ConfigureAwait(false);
            _producerClients.Clear();
        }
        finally
        {
            _producerClientLock.Release();
        }

        _cts.Dispose();
        _producerClientLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Publish loop (unified poll with adaptive backoff)
    // -------------------------------------------------------------------------

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<PostgreSqlOutboxRow> batch = await LeaseBatchAsync(ct).ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    _currentPollIntervalMs = Math.Min(
                        _currentPollIntervalMs * 2,
                        _options.MaxPollIntervalMs);

                    await Task.Delay(_currentPollIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                _currentPollIntervalMs = _options.MinPollIntervalMs;

                var poison = batch.Where(r => r.RetryCount >= _options.MaxRetryCount).ToList();
                foreach (var row in poison)
                {
                    try
                    {
                        await DeadLetterSingleRowAsync(row, _lastPublishError ?? "MaxRetryCount exceeded", ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OnError($"InlineDeadLetter(Seq={row.SequenceNumber})", ex);
                    }
                }

                var publishable = poison.Count > 0
                    ? batch.Where(r => r.RetryCount < _options.MaxRetryCount).ToList()
                    : batch;

                if (publishable.Count > 0)
                    await PublishBatchAsync(publishable, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError("PublishLoop", ex);
                await Task.Delay(_options.MinPollIntervalMs, ct).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Heartbeat loop
    // -------------------------------------------------------------------------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatIntervalMs, ct).ConfigureAwait(false);
                await RefreshHeartbeatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError("HeartbeatLoop", ex);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Rebalance loop
    // -------------------------------------------------------------------------

    private async Task RebalanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RebalanceIntervalMs, ct).ConfigureAwait(false);
                await RebalanceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError("RebalanceLoop", ex);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Orphan sweep loop
    // -------------------------------------------------------------------------

    private async Task OrphanSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.OrphanSweepIntervalMs, ct).ConfigureAwait(false);
                await ClaimOrphanPartitionsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError("OrphanSweepLoop", ex);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Dead-letter sweep loop
    // -------------------------------------------------------------------------

    private async Task DeadLetterSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.DeadLetterSweepIntervalMs, ct).ConfigureAwait(false);
                await SweepDeadLettersAsync(lastError: _lastPublishError, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError("DeadLetterSweepLoop", ex);
            }
        }
    }

    // -------------------------------------------------------------------------
    // SQL operations (PostgreSQL via Npgsql)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Combined lease query: picks up both fresh (unleased) and expired-lease rows
    /// in a single pass, ordered by event_datetime_utc, event_ordinal. This guarantees
    /// that events are published in application-controlled causal order, regardless
    /// of the database IDENTITY insertion order.
    ///
    /// Uses FOR UPDATE SKIP LOCKED (PostgreSQL equivalent of ROWLOCK + READPAST)
    /// and UPDATE...RETURNING (equivalent of OUTPUT inserted.*).
    ///
    /// retry_count is only incremented for rows that were previously leased (recovery).
    /// Fresh rows (leased_until_utc IS NULL) keep retry_count = 0.
    /// </summary>
    private async Task<IReadOnlyList<PostgreSqlOutboxRow>> LeaseBatchAsync(CancellationToken ct)
    {
        const string sql = @"
WITH batch AS (
    SELECT o.sequence_number
    FROM outbox o
    INNER JOIN outbox_partitions op
        ON  op.owner_producer_id = @publisher_id
        AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < NOW())
        AND (ABS(hashtext(o.partition_key)) % @total_partitions) = op.partition_id
    WHERE (o.leased_until_utc IS NULL OR o.leased_until_utc < NOW())
      AND o.retry_count < @max_retry_count
    ORDER BY o.event_datetime_utc, o.event_ordinal
    LIMIT @batch_size
    FOR UPDATE OF o SKIP LOCKED
)
UPDATE outbox o
SET    leased_until_utc = NOW() + make_interval(secs => @lease_duration_seconds),
       lease_owner      = @publisher_id,
       retry_count      = CASE WHEN o.leased_until_utc IS NOT NULL
                               THEN o.retry_count + 1
                               ELSE o.retry_count END
FROM   batch b
WHERE  o.sequence_number = b.sequence_number
RETURNING o.sequence_number, o.topic_name, o.partition_key, o.event_type,
          o.headers, o.payload, o.event_datetime_utc, o.event_ordinal,
          o.retry_count;";

        int totalPartitions = _totalPartitionCount;
        if (totalPartitions == 0)
            return Array.Empty<PostgreSqlOutboxRow>();

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@batch_size", _options.BatchSize);
                cmd.Parameters.AddWithValue("@lease_duration_seconds", (double)_options.LeaseDurationSeconds);
                cmd.Parameters.AddWithValue("@publisher_id", _producerId);
                cmd.Parameters.AddWithValue("@total_partitions", totalPartitions);
                cmd.Parameters.AddWithValue("@max_retry_count", _options.MaxRetryCount);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;

                var rows = new List<PostgreSqlOutboxRow>();
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add(new PostgreSqlOutboxRow(
                        SequenceNumber: reader.GetInt64(0),
                        TopicName: reader.GetString(1),
                        PartitionKey: reader.GetString(2),
                        EventType: reader.GetString(3),
                        Headers: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Payload: reader.GetString(5),
                        EventDateTimeUtc: reader.GetDateTime(6),
                        EventOrdinal: reader.GetInt16(7),
                        RetryCount: reader.GetInt32(8)));
                }

                return rows;
            }
            catch (NpgsqlException ex) when (IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Deletes published rows using ANY(@ids::bigint[]).
    /// The lease_owner guard prevents a zombie publisher from deleting rows
    /// re-leased to another instance.
    /// </summary>
    private async Task DeletePublishedRowsAsync(
        IEnumerable<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
DELETE FROM outbox
WHERE  sequence_number = ANY(@published_ids)
  AND  lease_owner = @publisher_id;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@publisher_id", _producerId);
                cmd.Parameters.AddWithValue("@published_ids", sequenceNumbers.ToArray());

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (NpgsqlException ex) when (IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Releases the lease on rows that were skipped (e.g. circuit breaker open).
    /// Resets leased_until_utc and lease_owner to NULL so the rows return to the
    /// unleased pool without incrementing retry_count.
    /// </summary>
    private async Task ReleaseLeasedRowsAsync(
        IEnumerable<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
UPDATE outbox
SET    leased_until_utc = NULL,
       lease_owner      = NULL
WHERE  sequence_number = ANY(@ids)
  AND  lease_owner = @publisher_id;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@publisher_id", _producerId);
                cmd.Parameters.AddWithValue("@ids", sequenceNumbers.ToArray());

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (NpgsqlException ex) when (IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Moves rows that have exceeded MaxRetryCount to outbox_dead_letter.
    /// Uses CTE with DELETE...RETURNING piped into INSERT (PostgreSQL equivalent
    /// of SQL Server's DELETE...OUTPUT INTO).
    /// </summary>
    private async Task SweepDeadLettersAsync(string? lastError, CancellationToken ct)
    {
        const string sql = @"
WITH dead AS (
    DELETE FROM outbox o
    USING (
        SELECT sequence_number
        FROM outbox
        WHERE retry_count >= @max_retry_count
          AND (leased_until_utc IS NULL OR leased_until_utc < NOW())
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
       NOW(), @last_error
FROM dead;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@max_retry_count", _options.MaxRetryCount);
                cmd.Parameters.Add(new NpgsqlParameter("@last_error", NpgsqlDbType.Varchar, 2000)
                {
                    Value = (object?)lastError ?? DBNull.Value
                });

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (NpgsqlException ex) when (IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Moves a single specific row to dead-letter immediately.
    /// </summary>
    private async Task DeadLetterSingleRowAsync(PostgreSqlOutboxRow row, string reason, CancellationToken ct)
    {
        const string sql = @"
WITH dead AS (
    DELETE FROM outbox
    WHERE sequence_number = @sequence_number
      AND lease_owner = @publisher_id
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
       NOW(), @last_error
FROM dead;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@sequence_number", row.SequenceNumber);
                cmd.Parameters.AddWithValue("@publisher_id", _producerId);
                cmd.Parameters.Add(new NpgsqlParameter("@last_error", NpgsqlDbType.Varchar, 2000)
                {
                    Value = (object?)reason ?? DBNull.Value
                });

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (NpgsqlException ex) when (IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RegisterProducerAsync(CancellationToken ct)
    {
        const string sql = @"
INSERT INTO outbox_producers (producer_id, registered_at_utc, last_heartbeat_utc, host_name)
VALUES (@producer_id, NOW(), NOW(), @host_name)
ON CONFLICT (producer_id) DO UPDATE
SET last_heartbeat_utc = NOW(),
    host_name          = EXCLUDED.host_name;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@producer_id", _producerId);
        cmd.Parameters.AddWithValue("@host_name", _hostName);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshHeartbeatAsync(CancellationToken ct)
    {
        const string sql = @"
UPDATE outbox_producers
SET    last_heartbeat_utc = NOW()
WHERE  producer_id = @producer_id;

UPDATE outbox_partitions
SET    grace_expires_utc = NULL
WHERE  owner_producer_id = @producer_id
  AND  grace_expires_utc IS NOT NULL;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@producer_id", _producerId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task UnregisterProducerAsync(CancellationToken ct)
    {
        const string releaseSql = @"
UPDATE outbox_partitions
SET    owner_producer_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
WHERE  owner_producer_id = @producer_id;";

        const string deleteSql = @"
DELETE FROM outbox_producers
WHERE  producer_id = @producer_id;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var releaseCmd = new NpgsqlCommand(releaseSql, conn, tx);
        releaseCmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        releaseCmd.Parameters.AddWithValue("@producer_id", _producerId);
        await releaseCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var deleteCmd = new NpgsqlCommand(deleteSql, conn, tx);
        deleteCmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        deleteCmd.Parameters.AddWithValue("@producer_id", _producerId);
        await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> GetTotalPartitionCountAsync(CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM outbox_partitions;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private async Task RebalanceAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Step 1: Mark stale producers' partitions as entering grace period
        const string markStaleSql = @"
UPDATE outbox_partitions
SET    grace_expires_utc = NOW() + make_interval(secs => @partition_grace_period_seconds)
WHERE  owner_producer_id <> @producer_id
  AND  owner_producer_id IS NOT NULL
  AND  grace_expires_utc IS NULL
  AND  owner_producer_id NOT IN (
           SELECT producer_id
           FROM   outbox_producers
           WHERE  last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)
       );";

        await using (var cmd = new NpgsqlCommand(markStaleSql, conn, tx))
        {
            cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@producer_id", _producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_options.HeartbeatTimeoutSeconds);
            cmd.Parameters.AddWithValue("@partition_grace_period_seconds", (double)_options.PartitionGracePeriodSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 2: Calculate fair share and claim unowned/grace-expired partitions
        const string claimSql = @"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
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
    WHERE (op.owner_producer_id IS NULL OR op.grace_expires_utc < NOW())
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE outbox_partitions
SET    owner_producer_id = @producer_id,
       owned_since_utc   = NOW(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  outbox_partitions.partition_id = to_claim.partition_id;";

        await using (var cmd = new NpgsqlCommand(claimSql, conn, tx))
        {
            cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@producer_id", _producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_options.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 3: Release excess above fair share
        const string releaseSql = @"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
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

        await using (var cmd = new NpgsqlCommand(releaseSql, conn, tx))
        {
            cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@producer_id", _producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_options.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task ClaimOrphanPartitionsAsync(CancellationToken ct)
    {
        const string sql = @"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
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
       owned_since_utc   = NOW(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  outbox_partitions.partition_id = to_claim.partition_id;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@producer_id", _producerId);
        cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_options.HeartbeatTimeoutSeconds);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // EventHub publish
    // -------------------------------------------------------------------------

    private async Task PublishBatchAsync(
        IReadOnlyList<PostgreSqlOutboxRow> rows, CancellationToken ct)
    {
        var byTopicAndKey = rows.GroupBy(r => (r.TopicName, r.PartitionKey));
        var published = new List<long>(rows.Count);

        foreach (var group in byTopicAndKey)
        {
            string topicName = group.Key.TopicName;
            string partitionKey = group.Key.PartitionKey;

            if (IsCircuitOpen(topicName))
            {
                OnError($"Circuit open for topic '{topicName}', releasing leased rows", null);
                await ReleaseLeasedRowsAsync(group.Select(r => r.SequenceNumber), ct)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                EventHubProducerClient producer = await GetOrCreateProducerAsync(topicName, ct)
                    .ConfigureAwait(false);

                var batches = await BuildEventHubBatchesAsync(producer, partitionKey, group, ct)
                    .ConfigureAwait(false);

                var groupIds = new List<long>();
                bool allSucceeded = true;

                foreach ((EventDataBatch eventBatch, List<long> batchSequenceNumbers) in batches)
                {
                    await using (eventBatch)
                    {
                        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        sendCts.CancelAfter(TimeSpan.FromSeconds(_options.EventHubSendTimeoutSeconds));

                        try
                        {
                            await producer.SendAsync(eventBatch, sendCts.Token).ConfigureAwait(false);
                            groupIds.AddRange(batchSequenceNumbers);
                            RecordSendSuccess(topicName);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            _lastPublishError = $"[{topicName}/{partitionKey}] EventHub send timeout";
                            OnError($"EventHub send timeout for topic '{topicName}' key '{partitionKey}'", null);
                            RecordSendFailure(topicName);
                            allSucceeded = false;
                            break;
                        }
                    }
                }

                if (allSucceeded)
                    published.AddRange(groupIds);
            }
            catch (Exception ex)
            {
                _lastPublishError = $"[{topicName}/{partitionKey}] {ex.GetType().Name}: {ex.Message}";
                OnError($"EventHub publish error for topic '{topicName}' key '{partitionKey}'", ex);
                RecordSendFailure(topicName);
            }
        }

        if (published.Count > 0)
        {
            await DeletePublishedRowsAsync(published, ct).ConfigureAwait(false);
        }
    }

    private async Task<List<(EventDataBatch Batch, List<long> SequenceNumbers)>> BuildEventHubBatchesAsync(
        EventHubProducerClient producer,
        string partitionKey,
        IEnumerable<PostgreSqlOutboxRow> rows,
        CancellationToken ct)
    {
        var result = new List<(EventDataBatch, List<long>)>();
        var batchOptions = new CreateBatchOptions
        {
            MaximumSizeInBytes = _options.EventHubMaxBatchBytes,
            PartitionKey = partitionKey
        };

        EventDataBatch? currentBatch = null;
        var currentIds = new List<long>();

        try
        {
            currentBatch = await producer
                .CreateBatchAsync(batchOptions, ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                EventData eventData = BuildEventData(row);

                if (!currentBatch.TryAdd(eventData))
                {
                    if (currentBatch.Count > 0)
                    {
                        result.Add((currentBatch, currentIds));
                        currentBatch = null;
                    }
                    else
                    {
                        currentBatch.Dispose();
                        currentBatch = null;
                    }

                    currentBatch = await producer
                        .CreateBatchAsync(batchOptions, ct)
                        .ConfigureAwait(false);
                    currentIds = new List<long>();

                    if (!currentBatch.TryAdd(eventData))
                    {
                        await DeadLetterSingleRowAsync(row,
                            $"Message SequenceNumber={row.SequenceNumber} exceeds EventHub max batch size ({_options.EventHubMaxBatchBytes} bytes)",
                            ct).ConfigureAwait(false);
                        continue;
                    }
                }

                currentIds.Add(row.SequenceNumber);
            }

            if (currentBatch is not null && currentBatch.Count > 0)
            {
                result.Add((currentBatch, currentIds));
                currentBatch = null;
            }
        }
        catch
        {
            foreach (var (batch, _) in result)
                batch.Dispose();
            result.Clear();
            throw;
        }
        finally
        {
            currentBatch?.Dispose();
        }

        return result;
    }

    private EventData BuildEventData(PostgreSqlOutboxRow row)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(row.Payload);
        var eventData = new EventData(body);
        eventData.Properties["EventType"] = row.EventType;
        eventData.Properties["PartitionKey"] = row.PartitionKey;

        if (row.Headers is not null)
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers);
                if (headers is not null)
                {
                    foreach (var (key, value) in headers)
                        eventData.Properties[key] = value;
                }
            }
            catch (JsonException ex)
            {
                _options.OnError?.Invoke(
                    $"Malformed headers JSON on SequenceNumber={row.SequenceNumber}, headers skipped", ex);
            }
        }

        return eventData;
    }

    // -------------------------------------------------------------------------
    // EventHub producer client management
    // -------------------------------------------------------------------------

    private async Task<EventHubProducerClient> GetOrCreateProducerAsync(
        string topicName, CancellationToken ct)
    {
        await _producerClientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_producerClients.TryGetValue(topicName, out var existing))
                return existing;

            var client = new EventHubProducerClient(
                _eventHubConnectionString,
                topicName,
                new EventHubProducerClientOptions
                {
                    RetryOptions = new EventHubsRetryOptions
                    {
                        MaximumRetries = 3,
                        Delay = TimeSpan.FromMilliseconds(500),
                        MaximumDelay = TimeSpan.FromSeconds(5),
                        Mode = EventHubsRetryMode.Exponential
                    }
                });

            _producerClients[topicName] = client;
            return client;
        }
        finally
        {
            _producerClientLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Circuit breaker
    // -------------------------------------------------------------------------

    private bool IsCircuitOpen(string topicName)
    {
        lock (_circuitLock)
        {
            if (_topicCircuitOpenUntil.TryGetValue(topicName, out var openUntil))
            {
                if (DateTime.UtcNow < openUntil)
                    return true;
                _topicCircuitOpenUntil.Remove(topicName);
            }
            return false;
        }
    }

    private void RecordSendSuccess(string topicName)
    {
        lock (_circuitLock)
        {
            _topicFailureCount.Remove(topicName);
            _topicCircuitOpenUntil.Remove(topicName);
        }
    }

    private void RecordSendFailure(string topicName)
    {
        lock (_circuitLock)
        {
            _topicFailureCount.TryGetValue(topicName, out int count);
            count++;
            _topicFailureCount[topicName] = count;

            if (count >= _options.CircuitBreakerFailureThreshold)
            {
                var openUntil = DateTime.UtcNow.AddSeconds(_options.CircuitBreakerOpenDurationSeconds);
                _topicCircuitOpenUntil[topicName] = openUntil;
                OnError($"Circuit breaker OPEN for topic '{topicName}' until {openUntil:O}", null);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Identifies transient PostgreSQL errors that warrant a retry.
    /// </summary>
    private static bool IsTransientNpgsqlError(NpgsqlException ex)
    {
        // PostgreSQL SQLSTATE codes for transient errors:
        // 40001 = serialization_failure (deadlock or serializable conflict)
        // 40P01 = deadlock_detected
        // 08006 = connection_failure
        // 08001 = sqlclient_unable_to_establish_sqlconnection
        // 08004 = sqlserver_rejected_establishment_of_sqlconnection
        // 57P03 = cannot_connect_now (server starting up)
        // 53300 = too_many_connections
        if (ex.SqlState is "40001" or "40P01" or "08006" or "08001" or "08004"
            or "57P03" or "53300")
            return true;

        // Also treat IOException/SocketException as transient (network blip)
        if (ex.InnerException is System.IO.IOException or System.Net.Sockets.SocketException)
            return true;

        return false;
    }

    private void OnError(string context, Exception? exception)
    {
        var handler = _options.OnError;
        if (handler is not null)
            handler(context, exception);
        else
            Console.Error.WriteLine($"[PostgreSqlOutboxPublisher] {context}: {exception?.Message}");
    }
}

// =============================================================================
// Configuration
// =============================================================================

/// <summary>
/// All tuning parameters for <see cref="PostgreSqlOutboxPublisher"/>.
/// Defaults are suitable for most workloads; adjust based on profiling.
/// </summary>
public sealed class PostgreSqlOutboxPublisherOptions
{
    /// <summary>Rows to lease per primary-poll call. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Seconds until a lease expires if the publisher does not delete the row.
    /// Must be comfortably larger than EventHubSendTimeoutSeconds.
    /// Default: 45.
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 45;

    /// <summary>
    /// After this many re-leases (from expired leases) without success, the row
    /// is moved to outbox_dead_letter. Default: 5.
    /// </summary>
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>Minimum adaptive poll interval in milliseconds. Default: 100.</summary>
    public int MinPollIntervalMs { get; set; } = 100;

    /// <summary>Maximum adaptive poll interval (idle backoff ceiling) in milliseconds. Default: 5000.</summary>
    public int MaxPollIntervalMs { get; set; } = 5000;

    /// <summary>Heartbeat renewal interval in milliseconds. Default: 10 000.</summary>
    public int HeartbeatIntervalMs { get; set; } = 10_000;

    /// <summary>
    /// Seconds without a heartbeat before a producer is considered dead.
    /// Default: 30.
    /// </summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Grace window in seconds for partition handover. Must exceed LeaseDurationSeconds
    /// to ensure in-flight leases from the outgoing owner expire before the
    /// new owner starts processing. Default: 60.
    /// </summary>
    public int PartitionGracePeriodSeconds { get; set; } = 60;

    /// <summary>How often to run the dead-letter sweep in milliseconds. Default: 60 000.</summary>
    public int DeadLetterSweepIntervalMs { get; set; } = 60_000;

    /// <summary>How often to scan for rows in unowned partitions in milliseconds. Default: 60 000.</summary>
    public int OrphanSweepIntervalMs { get; set; } = 60_000;

    /// <summary>How often to run the rebalance check in milliseconds. Default: 30 000.</summary>
    public int RebalanceIntervalMs { get; set; } = 30_000;

    /// <summary>Timeout for a single EventHub SendAsync call in seconds. Default: 15.</summary>
    public int EventHubSendTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Consecutive send failures before the circuit opens for a topic.
    /// Default: 3.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 3;

    /// <summary>
    /// Seconds to keep the circuit open before attempting a half-open probe.
    /// Default: 30.
    /// </summary>
    public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;

    /// <summary>Maximum EventHub batch size in bytes (1 MB). Default: 1 048 576.</summary>
    public long EventHubMaxBatchBytes { get; set; } = 1_048_576;

    /// <summary>SQL command timeout in seconds. Default: 30.</summary>
    public int SqlCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Error callback for logging/observability integration. Receives context string
    /// and optional exception. When null, errors are written to Console.Error.
    /// </summary>
    public Action<string, Exception?>? OnError { get; set; }
}

// =============================================================================
// Data models
// =============================================================================

/// <summary>A single row read from the outbox table (PostgreSQL).</summary>
internal sealed record PostgreSqlOutboxRow(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    DateTime EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount = 0);
