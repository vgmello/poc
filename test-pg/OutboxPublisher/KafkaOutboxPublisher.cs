using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;
using NpgsqlTypes;
using Outbox.Shared;

namespace Outbox.Publisher;

public sealed class KafkaOutboxPublisher : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _bootstrapServers;
    private readonly string _producerId;
    private readonly string _hostName;
    private readonly KafkaOutboxPublisherOptions _options;

    private IProducer<string, string>? _kafkaProducer;

    private CancellationTokenSource _cts = new();
    private Task _publishLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private Task _deadLetterSweepLoop = Task.CompletedTask;
    private Task _rebalanceLoop = Task.CompletedTask;
    private Task _orphanSweepLoop = Task.CompletedTask;

    private int _totalPartitionCount;

    private volatile int _currentPollIntervalMs;
    private volatile string? _lastPublishError;

    private readonly Dictionary<string, int> _topicFailureCount = new();
    private readonly Dictionary<string, DateTime> _topicCircuitOpenUntil = new();
    private readonly object _circuitLock = new();

    private bool _disposed;

    public KafkaOutboxPublisher(
        string connectionString,
        string bootstrapServers,
        KafkaOutboxPublisherOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));
        if (string.IsNullOrWhiteSpace(bootstrapServers))
            throw new ArgumentNullException(nameof(bootstrapServers));

        _connectionString = connectionString;
        _bootstrapServers = bootstrapServers;
        _options = options ?? new KafkaOutboxPublisherOptions();
        _producerId = $"{Environment.MachineName}:{System.Diagnostics.Process.GetCurrentProcess().Id}:{Guid.NewGuid():N}";
        _hostName = Environment.MachineName;
        _currentPollIntervalMs = _options.MinPollIntervalMs;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_options.PartitionGracePeriodSeconds <= _options.LeaseDurationSeconds)
            throw new InvalidOperationException(
                $"PartitionGracePeriodSeconds ({_options.PartitionGracePeriodSeconds}) must exceed LeaseDurationSeconds ({_options.LeaseDurationSeconds}).");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _kafkaProducer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = _bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 3,
            RetryBackoffMs = 500,
            LingerMs = 5,
        }).Build();

        await RegisterProducerAsync(_cts.Token).ConfigureAwait(false);

        _totalPartitionCount = await GetTotalPartitionCountAsync(_cts.Token).ConfigureAwait(false);
        if (_totalPartitionCount == 0)
            throw new InvalidOperationException(
                "outbox_partitions is empty. Run init.sql before starting publishers.");

        await RebalanceAsync(_cts.Token).ConfigureAwait(false);

        _heartbeatLoop = HeartbeatLoopAsync(_cts.Token);
        _rebalanceLoop = RebalanceLoopAsync(_cts.Token);
        _publishLoop = PublishLoopAsync(_cts.Token);
        _deadLetterSweepLoop = DeadLetterSweepLoopAsync(_cts.Token);
        _orphanSweepLoop = OrphanSweepLoopAsync(_cts.Token);
    }

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

        try { await StopAsync().ConfigureAwait(false); }
        catch { /* best effort */ }

        _kafkaProducer?.Dispose();
        _cts.Dispose();
    }

    // -------------------------------------------------------------------------
    // Background loops
    // -------------------------------------------------------------------------

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = await LeaseBatchAsync(ct).ConfigureAwait(false);

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

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatIntervalMs, ct).ConfigureAwait(false);
                await RefreshHeartbeatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("HeartbeatLoop", ex); }
        }
    }

    private async Task RebalanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RebalanceIntervalMs, ct).ConfigureAwait(false);
                await RebalanceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("RebalanceLoop", ex); }
        }
    }

    private async Task OrphanSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.OrphanSweepIntervalMs, ct).ConfigureAwait(false);
                await ClaimOrphanPartitionsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("OrphanSweepLoop", ex); }
        }
    }

    private async Task DeadLetterSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.DeadLetterSweepIntervalMs, ct).ConfigureAwait(false);
                await SweepDeadLettersAsync(_lastPublishError, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("DeadLetterSweepLoop", ex); }
        }
    }

    // -------------------------------------------------------------------------
    // Kafka publish
    // -------------------------------------------------------------------------

    private async Task PublishBatchAsync(IReadOnlyList<OutboxRow> rows, CancellationToken ct)
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
                var groupIds = new List<long>();
                bool allSucceeded = true;

                foreach (var row in group)
                {
                    var message = BuildKafkaMessage(row);

                    using var produceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    produceCts.CancelAfter(TimeSpan.FromSeconds(_options.KafkaProduceTimeoutSeconds));

                    try
                    {
                        await _kafkaProducer!.ProduceAsync(topicName, message, produceCts.Token)
                            .ConfigureAwait(false);
                        groupIds.Add(row.SequenceNumber);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _lastPublishError = $"[{topicName}/{partitionKey}] Kafka produce timeout";
                        OnError($"Kafka produce timeout for topic '{topicName}' key '{partitionKey}'", null);
                        RecordSendFailure(topicName);
                        allSucceeded = false;
                        break;
                    }
                    catch (ProduceException<string, string> ex)
                    {
                        _lastPublishError = $"[{topicName}/{partitionKey}] {ex.Error.Reason}";
                        OnError($"Kafka produce error for topic '{topicName}' key '{partitionKey}'", ex);
                        RecordSendFailure(topicName);
                        allSucceeded = false;
                        break;
                    }
                }

                if (allSucceeded)
                {
                    RecordSendSuccess(topicName);
                    published.AddRange(groupIds);
                }
            }
            catch (Exception ex)
            {
                _lastPublishError = $"[{topicName}/{partitionKey}] {ex.GetType().Name}: {ex.Message}";
                OnError($"Kafka publish error for topic '{topicName}' key '{partitionKey}'", ex);
                RecordSendFailure(topicName);
            }
        }

        if (published.Count > 0)
            await DeletePublishedRowsAsync(published, ct).ConfigureAwait(false);
    }

    private static Message<string, string> BuildKafkaMessage(OutboxRow row)
    {
        var headers = new Headers();
        headers.Add("event-type", Encoding.UTF8.GetBytes(row.EventType));

        if (row.Headers is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers);
                if (parsed is not null)
                {
                    foreach (var (key, value) in parsed)
                        headers.Add(key, Encoding.UTF8.GetBytes(value));
                }
            }
            catch (JsonException)
            {
                // Malformed headers: skip, event-type header is still present
            }
        }

        return new Message<string, string>
        {
            Key = row.PartitionKey,
            Value = row.Payload,
            Headers = headers
        };
    }

    // -------------------------------------------------------------------------
    // SQL operations (PostgreSQL via Npgsql)
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<OutboxRow>> LeaseBatchAsync(CancellationToken ct)
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
        if (totalPartitions == 0) return [];

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

                var rows = new List<OutboxRow>();
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add(new OutboxRow(
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
            catch (NpgsqlException ex) when (DbHelpers.IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DeletePublishedRowsAsync(IEnumerable<long> sequenceNumbers, CancellationToken ct)
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
            catch (NpgsqlException ex) when (DbHelpers.IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseLeasedRowsAsync(IEnumerable<long> sequenceNumbers, CancellationToken ct)
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
            catch (NpgsqlException ex) when (DbHelpers.IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

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
            catch (NpgsqlException ex) when (DbHelpers.IsTransientNpgsqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DeadLetterSingleRowAsync(OutboxRow row, string reason, CancellationToken ct)
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
            catch (NpgsqlException ex) when (DbHelpers.IsTransientNpgsqlError(ex) && attempt < 3)
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

        // Step 2: Claim unowned/grace-expired partitions up to fair share
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

    private void OnError(string context, Exception? exception)
    {
        var handler = _options.OnError;
        if (handler is not null)
            handler(context, exception);
        else
            Console.Error.WriteLine($"[KafkaOutboxPublisher] {context}: {exception?.Message}");
    }
}
