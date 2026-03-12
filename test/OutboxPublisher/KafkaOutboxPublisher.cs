using System.Data;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Data.SqlClient;
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

    private volatile int _consecutiveEmptyPolls;
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
                "dbo.OutboxPartitions is empty. Run init.sql before starting publishers.");

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
                    _consecutiveEmptyPolls++;
                    _currentPollIntervalMs = Math.Min(
                        _currentPollIntervalMs * 2,
                        _options.MaxPollIntervalMs);
                    await Task.Delay(_currentPollIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                _consecutiveEmptyPolls = 0;
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
                // All-or-nothing per (topic, key) group: only delete if every
                // produce succeeds. Preserves ordering invariant.
                var groupIds = new List<long>();
                bool allSucceeded = true;

                foreach (var row in group)
                {
                    var message = BuildKafkaMessage(row);

                    using var produceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    produceCts.CancelAfter(TimeSpan.FromSeconds(_options.KafkaProduceTimeoutSeconds));

                    try
                    {
                        // Note: Unlike EventHub's SendAsync, Kafka's ProduceAsync cancellation
                        // does not abort already-queued messages — timeout is best-effort only.
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
    // SQL operations
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<OutboxRow>> LeaseBatchAsync(CancellationToken ct)
    {
        const string sql = @"
WITH Batch AS
(
    SELECT TOP (@BatchSize)
        o.SequenceNumber,
        o.LeasedUntilUtc,
        o.LeaseOwner,
        o.RetryCount
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    INNER JOIN dbo.OutboxPartitions op
        ON  op.OwnerProducerId = @PublisherId
        AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
        AND (ABS(CHECKSUM(o.PartitionKey)) % @TotalPartitions) = op.PartitionId
    WHERE (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME())
      AND o.RetryCount < @MaxRetryCount
    ORDER BY o.SequenceNumber
)
UPDATE Batch
SET    LeasedUntilUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()),
       LeaseOwner     = @PublisherId,
       RetryCount     = CASE WHEN LeasedUntilUtc IS NOT NULL
                             THEN RetryCount + 1
                             ELSE RetryCount END
OUTPUT inserted.SequenceNumber,
       inserted.TopicName,
       inserted.PartitionKey,
       inserted.EventType,
       inserted.Headers,
       inserted.Payload,
       inserted.RetryCount;";

        int totalPartitions = _totalPartitionCount;
        if (totalPartitions == 0) return [];

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@BatchSize", _options.BatchSize);
                cmd.Parameters.AddWithValue("@LeaseDurationSeconds", _options.LeaseDurationSeconds);
                cmd.Parameters.AddWithValue("@PublisherId", _producerId);
                cmd.Parameters.AddWithValue("@TotalPartitions", totalPartitions);
                cmd.Parameters.AddWithValue("@MaxRetryCount", _options.MaxRetryCount);
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
                        RetryCount: reader.GetInt32(6)));
                }

                return rows;
            }
            catch (SqlException ex) when (DbHelpers.IsTransientSqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DeletePublishedRowsAsync(IEnumerable<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
DELETE o
FROM   dbo.Outbox o
INNER JOIN @PublishedIds p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new SqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@PublisherId", _producerId);

                var tvp = DbHelpers.BuildSequenceNumberTvp(sequenceNumbers);
                var tvpParam = cmd.Parameters.AddWithValue("@PublishedIds", tvp);
                tvpParam.SqlDbType = SqlDbType.Structured;
                tvpParam.TypeName = "dbo.SequenceNumberList";

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (DbHelpers.IsTransientSqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseLeasedRowsAsync(IEnumerable<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
UPDATE o
SET    o.LeasedUntilUtc = NULL,
       o.LeaseOwner     = NULL
FROM   dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new SqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@PublisherId", _producerId);

                var tvp = DbHelpers.BuildSequenceNumberTvp(sequenceNumbers);
                var tvpParam = cmd.Parameters.AddWithValue("@Ids", tvp);
                tvpParam.SqlDbType = SqlDbType.Structured;
                tvpParam.TypeName = "dbo.SequenceNumberList";

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (DbHelpers.IsTransientSqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SweepDeadLettersAsync(string? lastError, CancellationToken ct)
    {
        const string sql = @"
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.CreatedAtUtc, deleted.RetryCount, SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
WHERE o.RetryCount >= @MaxRetryCount
  AND (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME());";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@MaxRetryCount", _options.MaxRetryCount);
        cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
            (object?)lastError ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task DeadLetterSingleRowAsync(OutboxRow row, string reason, CancellationToken ct)
    {
        const string sql = @"
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.CreatedAtUtc, deleted.RetryCount, SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o
WHERE o.SequenceNumber = @SequenceNumber
  AND o.LeaseOwner = @PublisherId;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@SequenceNumber", row.SequenceNumber);
        cmd.Parameters.AddWithValue("@PublisherId", _producerId);
        cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
            (object?)reason ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RegisterProducerAsync(CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.OutboxProducers WITH (HOLDLOCK) AS target
USING (SELECT @ProducerId AS ProducerId, @HostName AS HostName) AS source
    ON target.ProducerId = source.ProducerId
WHEN MATCHED THEN
    UPDATE SET LastHeartbeatUtc = SYSUTCDATETIME(),
               HostName         = source.HostName
WHEN NOT MATCHED THEN
    INSERT (ProducerId, RegisteredAtUtc, LastHeartbeatUtc, HostName)
    VALUES (source.ProducerId, SYSUTCDATETIME(), SYSUTCDATETIME(), source.HostName);";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);
        cmd.Parameters.AddWithValue("@HostName", _hostName);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshHeartbeatAsync(CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.OutboxProducers
SET    LastHeartbeatUtc = SYSUTCDATETIME()
WHERE  ProducerId = @ProducerId;

UPDATE dbo.OutboxPartitions
SET    GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId
  AND  GraceExpiresUtc IS NOT NULL;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task UnregisterProducerAsync(CancellationToken ct)
    {
        const string releaseSql = @"
UPDATE dbo.OutboxPartitions
SET    OwnerProducerId = NULL,
       OwnedSinceUtc  = NULL,
       GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId;";

        const string deleteSql = @"
DELETE FROM dbo.OutboxProducers
WHERE  ProducerId = @ProducerId;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var releaseCmd = new SqlCommand(releaseSql, conn, tx);
        releaseCmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        releaseCmd.Parameters.AddWithValue("@ProducerId", _producerId);
        await releaseCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var deleteCmd = new SqlCommand(deleteSql, conn, tx);
        deleteCmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        deleteCmd.Parameters.AddWithValue("@ProducerId", _producerId);
        await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> GetTotalPartitionCountAsync(CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM dbo.OutboxPartitions;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private async Task RebalanceAsync(CancellationToken ct)
    {
        const string sql = @"
BEGIN TRANSACTION;

DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

IF @ToAcquire > 0
BEGIN
    UPDATE dbo.OutboxPartitions
    SET    GraceExpiresUtc = DATEADD(SECOND, @PartitionGracePeriodSeconds, SYSUTCDATETIME())
    WHERE  OwnerProducerId <> @ProducerId
      AND  OwnerProducerId IS NOT NULL
      AND  GraceExpiresUtc IS NULL
      AND  OwnerProducerId NOT IN
           (
               SELECT ProducerId
               FROM   dbo.OutboxProducers
               WHERE  LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
           );

    UPDATE TOP (@ToAcquire) dbo.OutboxPartitions WITH (UPDLOCK)
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    WHERE  (OwnerProducerId IS NULL
            OR GraceExpiresUtc < SYSUTCDATETIME());
END;

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

IF @CurrentlyOwned > @FairShare
BEGIN
    DECLARE @ToRelease INT = @CurrentlyOwned - @FairShare;

    UPDATE TOP (@ToRelease) dbo.OutboxPartitions
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId = @ProducerId;
END;

COMMIT TRANSACTION;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);
        cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _options.HeartbeatTimeoutSeconds);
        cmd.Parameters.AddWithValue("@PartitionGracePeriodSeconds", _options.PartitionGracePeriodSeconds);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task ClaimOrphanPartitionsAsync(CancellationToken ct)
    {
        const string sql = @"
DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

IF @ToAcquire > 0
BEGIN
    UPDATE TOP (@ToAcquire) dbo.OutboxPartitions WITH (UPDLOCK)
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId IS NULL;
END;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);
        cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _options.HeartbeatTimeoutSeconds);

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
