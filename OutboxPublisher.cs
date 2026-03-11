using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

/// <summary>
/// Polls dbo.Outbox, publishes leased rows to Azure EventHub, then deletes them.
///
/// Features:
///   - Partition affinity: each publisher owns a subset of EventHub partitions,
///     ensuring per-partition-key ordering (see §4 of EventHubOutboxSpec.md).
///   - Dynamic rebalance: fair-share partition assignment adjusts automatically
///     as instances join or leave.
///   - Heartbeat: keeps dbo.OutboxProducers up-to-date so peers detect crashes.
///   - Recovery: expired-lease rows are re-leased with an incremented RetryCount.
///   - Dead-letter sweep: rows exceeding MaxRetryCount are moved to
///     dbo.OutboxDeadLetter, isolating poison messages.
///   - Adaptive polling: exponential backoff on empty polls; instant reset on
///     the first non-empty poll.
///   - TVP deletes: uses dbo.SequenceNumberList TVP for efficient batch deletes
///     with correct cardinality estimates (replaces OPENJSON).
///   - EventHub batch limits: splits each topic batch to stay under 1 MB.
///   - Producer lifecycle: clean registration, heartbeat, and graceful shutdown.
/// </summary>
public sealed class OutboxPublisher : IAsyncDisposable
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    private readonly string _connectionString;
    private readonly string _eventHubConnectionString;
    private readonly string _producerId;
    private readonly string _hostName;
    private readonly OutboxPublisherOptions _options;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private readonly Dictionary<string, EventHubProducerClient> _producerClients = new();
    private readonly SemaphoreSlim _producerClientLock = new(1, 1);

    private CancellationTokenSource _cts = new();
    private Task _publishLoop = Task.CompletedTask;
    private Task _recoveryLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private Task _deadLetterSweepLoop = Task.CompletedTask;
    private Task _rebalanceLoop = Task.CompletedTask;
    private Task _orphanSweepLoop = Task.CompletedTask;

    // Current owned partition IDs. Refreshed after each rebalance.
    private IReadOnlyList<int> _ownedPartitions = Array.Empty<int>();
    private int _totalPartitionCount = 0;
    private readonly SemaphoreSlim _partitionLock = new(1, 1);

    // Adaptive backoff state for the primary poll loop.
    private volatile int _consecutiveEmptyPolls = 0;
    private volatile int _currentPollIntervalMs;

    // Last known error per topic, for dead-letter diagnostics.
    private volatile string? _lastPublishError;

    // Circuit breaker: tracks consecutive failures per topic.
    private readonly Dictionary<string, int> _topicFailureCount = new();
    private readonly Dictionary<string, DateTime> _topicCircuitOpenUntil = new();
    private readonly object _circuitLock = new();

    private bool _disposed;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public OutboxPublisher(
        string connectionString,
        string eventHubConnectionString,
        OutboxPublisherOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));
        if (string.IsNullOrWhiteSpace(eventHubConnectionString))
            throw new ArgumentNullException(nameof(eventHubConnectionString));

        _connectionString = connectionString;
        _eventHubConnectionString = eventHubConnectionString;
        _options = options ?? new OutboxPublisherOptions();
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
                "dbo.OutboxPartitions is empty. Run the partition initialisation script (EventHubOutbox.sql §6a) before starting publishers.");

        await RebalanceAsync(_cts.Token).ConfigureAwait(false);

        _heartbeatLoop = HeartbeatLoopAsync(_cts.Token);
        _rebalanceLoop = RebalanceLoopAsync(_cts.Token);
        _publishLoop = PublishLoopAsync(_cts.Token);
        _recoveryLoop = RecoveryLoopAsync(_cts.Token);
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
                _recoveryLoop,
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
        _partitionLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Primary publish loop (adaptive polling)
    // -------------------------------------------------------------------------

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<OutboxRow> batch = await LeaseUnleasedBatchAsync(ct).ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    // Adaptive backoff on empty poll.
                    _consecutiveEmptyPolls++;
                    _currentPollIntervalMs = Math.Min(
                        _currentPollIntervalMs * 2,
                        _options.MaxPollIntervalMs);

                    await DelayAsync(_currentPollIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                // Non-empty poll: reset backoff.
                _consecutiveEmptyPolls = 0;
                _currentPollIntervalMs = _options.MinPollIntervalMs;

                await PublishBatchAsync(batch, lastError: null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log and continue — individual batch failures must not crash the loop.
                OnError("PublishLoop", ex);
                await DelayAsync(_options.MinPollIntervalMs, ct).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Recovery loop (fixed interval)
    // -------------------------------------------------------------------------

    private async Task RecoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DelayAsync(_options.RecoveryPollIntervalMs, ct).ConfigureAwait(false);

                IReadOnlyList<OutboxRow> batch = await LeaseExpiredBatchAsync(ct).ConfigureAwait(false);
                if (batch.Count > 0)
                    await PublishBatchAsync(batch, lastError: null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError("RecoveryLoop", ex);
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
                await DelayAsync(_options.HeartbeatIntervalMs, ct).ConfigureAwait(false);
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
                await DelayAsync(_options.RebalanceIntervalMs, ct).ConfigureAwait(false);
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
                await DelayAsync(_options.OrphanSweepIntervalMs, ct).ConfigureAwait(false);
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

        // Refresh local partition list after claiming orphans.
        const string getOwnedSql = @"
SELECT PartitionId
FROM   dbo.OutboxPartitions
WHERE  OwnerProducerId = @OwnerId
  AND  (GraceExpiresUtc IS NULL OR GraceExpiresUtc < SYSUTCDATETIME());";

        var owned = new List<int>();
        await using var cmd2 = new SqlCommand(getOwnedSql, conn);
        cmd2.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd2.Parameters.AddWithValue("@OwnerId", _producerId);

        await using var reader = await cmd2.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            owned.Add(reader.GetInt32(0));

        await _partitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ownedPartitions = owned;
        }
        finally
        {
            _partitionLock.Release();
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
                await DelayAsync(_options.DeadLetterSweepIntervalMs, ct).ConfigureAwait(false);
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
    // SQL operations
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<OutboxRow>> LeaseUnleasedBatchAsync(CancellationToken ct)
    {
        // Partition affinity join: routes rows to their EventHub partition bucket using
        // ABS(CHECKSUM(PartitionKey)) % TotalPartitions. This is a non-SARGable expression
        // (cannot seek on IX_Outbox_Partition) because it is computed per-row. The trade-off
        // is intentional: the primary filter is LeasedUntilUtc IS NULL (served by
        // IX_Outbox_Unleased) and the partition join is a secondary reduction over the small
        // unleased set. For very high partition counts or strict latency budgets, store a
        // pre-computed PartitionBucket INT column on dbo.Outbox to enable seeks.
        const string sql = @"
WITH Batch AS
(
    SELECT TOP (@BatchSize)
        o.SequenceNumber,
        o.LeasedUntilUtc,
        o.LeaseOwner
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    INNER JOIN dbo.OutboxPartitions op
        ON  op.OwnerProducerId = @PublisherId
        AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
        AND (ABS(CHECKSUM(o.PartitionKey)) % @TotalPartitions) = op.PartitionId
    WHERE o.LeasedUntilUtc IS NULL
    ORDER BY o.SequenceNumber
)
UPDATE Batch
SET    LeasedUntilUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()),
       LeaseOwner     = @PublisherId
OUTPUT inserted.SequenceNumber,
       inserted.TopicName,
       inserted.PartitionKey,
       inserted.EventType,
       inserted.Headers,
       inserted.Payload;";

        return await ExecuteLeaseQueryAsync(sql, incrementRetry: false, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OutboxRow>> LeaseExpiredBatchAsync(CancellationToken ct)
    {
        const string sql = @"
WITH Expired AS
(
    SELECT TOP (@BatchSize)
        SequenceNumber,
        LeasedUntilUtc,
        LeaseOwner,
        RetryCount
    FROM dbo.Outbox WITH (ROWLOCK, READPAST)
    WHERE LeasedUntilUtc IS NOT NULL
      AND LeasedUntilUtc < SYSUTCDATETIME()
      AND RetryCount < @MaxRetryCount
    ORDER BY SequenceNumber
)
UPDATE Expired
SET    LeasedUntilUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()),
       LeaseOwner     = @PublisherId,
       RetryCount     = RetryCount + 1
OUTPUT inserted.SequenceNumber,
       inserted.TopicName,
       inserted.PartitionKey,
       inserted.EventType,
       inserted.Headers,
       inserted.Payload;";

        return await ExecuteLeaseQueryAsync(sql, incrementRetry: true, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OutboxRow>> ExecuteLeaseQueryAsync(
        string sql, bool incrementRetry, CancellationToken ct)
    {
        await _partitionLock.WaitAsync(ct).ConfigureAwait(false);
        int totalPartitions;
        try
        {
            totalPartitions = _totalPartitionCount;
        }
        finally
        {
            _partitionLock.Release();
        }

        if (totalPartitions == 0)
            return Array.Empty<OutboxRow>();

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
                Payload: reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>
    /// Deletes published rows using the dbo.SequenceNumberList TVP.
    /// The LeaseOwner guard prevents a zombie publisher from deleting rows
    /// re-leased to another instance.
    /// </summary>
    private async Task DeletePublishedRowsAsync(
        IEnumerable<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
DELETE o
FROM   dbo.Outbox o
INNER JOIN @PublishedIds p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@PublisherId", _producerId);

        var tvp = BuildSequenceNumberTvp(sequenceNumbers);
        var tvpParam = cmd.Parameters.AddWithValue("@PublishedIds", tvp);
        tvpParam.SqlDbType = SqlDbType.Structured;
        tvpParam.TypeName = "dbo.SequenceNumberList";

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves rows that have exceeded MaxRetryCount to dbo.OutboxDeadLetter.
    /// Called by the dead-letter sweep loop and directly by the publisher when
    /// it encounters a row it already knows is a poison message.
    /// </summary>
    private async Task SweepDeadLettersAsync(string? lastError, CancellationToken ct)
    {
        const string sql = @"
BEGIN TRANSACTION;

    DELETE o
    OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
           deleted.EventType, deleted.Headers, deleted.Payload,
           deleted.CreatedAtUtc, deleted.RetryCount, SYSUTCDATETIME(), @LastError
    INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
         Headers, Payload, CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    WHERE o.RetryCount >= @MaxRetryCount
      AND (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME());

COMMIT TRANSACTION;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@MaxRetryCount", _options.MaxRetryCount);
        cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
            (object?)lastError ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a single specific row to dead-letter immediately, without waiting for
    /// RetryCount to be exhausted. Used for rows that can never be published (e.g.,
    /// payload too large for any EventHub batch).
    /// </summary>
    private async Task DeadLetterSingleRowAsync(OutboxRow row, string reason, CancellationToken ct)
    {
        const string sql = @"
BEGIN TRANSACTION;
    INSERT INTO dbo.OutboxDeadLetter
        (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload,
         CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
    SELECT
        SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload,
        CreatedAtUtc, RetryCount, SYSUTCDATETIME(), @LastError
    FROM dbo.Outbox
    WHERE SequenceNumber = @SequenceNumber
      AND LeaseOwner = @PublisherId;

    DELETE FROM dbo.Outbox
    WHERE  SequenceNumber = @SequenceNumber
      AND  LeaseOwner     = @PublisherId;
COMMIT TRANSACTION;";

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
WHERE  ProducerId = @ProducerId;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task UnregisterProducerAsync(CancellationToken ct)
    {
        const string sql = @"
BEGIN TRANSACTION;
    UPDATE dbo.OutboxPartitions
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId = @ProducerId;

    DELETE FROM dbo.OutboxProducers
    WHERE  ProducerId = @ProducerId;
COMMIT TRANSACTION;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        // Step 1: Mark stale (crashed) producers' partitions as entering grace period.
        // Step 2: Claim unowned / grace-expired partitions up to fair share.
        // Step 3: Release excess partitions above fair share.
        // Step 4: Refresh local owned-partition list.

        const string claimSql = @"
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
END;";

        const string releaseSql = @"
DECLARE @TotalPartitions INT;
DECLARE @ActiveProducers INT;
DECLARE @FairShare       INT;
DECLARE @CurrentlyOwned  INT;
DECLARE @ToRelease       INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToRelease = @CurrentlyOwned - @FairShare;

IF @ToRelease > 0
BEGIN
    UPDATE TOP (@ToRelease) dbo.OutboxPartitions
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId = @ProducerId;
END;";

        const string getOwnedSql = @"
SELECT PartitionId
FROM   dbo.OutboxPartitions
WHERE  OwnerProducerId = @ProducerId
  AND  (GraceExpiresUtc IS NULL OR GraceExpiresUtc < SYSUTCDATETIME());";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new SqlCommand(claimSql, conn))
        {
            cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", _producerId);
            cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _options.HeartbeatTimeoutSeconds);
            cmd.Parameters.AddWithValue("@PartitionGracePeriodSeconds", _options.PartitionGracePeriodSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new SqlCommand(releaseSql, conn))
        {
            cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", _producerId);
            cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _options.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var owned = new List<int>();
        await using (var cmd = new SqlCommand(getOwnedSql, conn))
        {
            cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", _producerId);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                owned.Add(reader.GetInt32(0));
        }

        await _partitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ownedPartitions = owned;
        }
        finally
        {
            _partitionLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // EventHub publish
    // -------------------------------------------------------------------------

    /// <summary>
    /// Groups rows by TopicName and publishes each group.
    /// Splits each topic's rows into EventHub batches that fit within the 1 MB limit.
    /// Deletes successfully published rows using the TVP delete.
    /// Moves poison messages to dead-letter on persistent failure.
    /// </summary>
    private async Task PublishBatchAsync(
        IReadOnlyList<OutboxRow> rows, string? lastError, CancellationToken ct)
    {
        var byTopicAndKey = rows.GroupBy(r => (r.TopicName, r.PartitionKey));
        var published = new List<long>(rows.Count);

        foreach (var group in byTopicAndKey)
        {
            string topicName = group.Key.TopicName;
            string partitionKey = group.Key.PartitionKey;

            if (IsCircuitOpen(topicName))
            {
                OnError($"Circuit open for topic '{topicName}', skipping batch (rows will be retried after circuit closes)", null);
                continue;
            }

            try
            {
                EventHubProducerClient producer = await GetOrCreateProducerAsync(topicName, ct)
                    .ConfigureAwait(false);

                var batches = await BuildEventHubBatchesAsync(producer, partitionKey, group, ct)
                    .ConfigureAwait(false);

                foreach ((EventDataBatch eventBatch, List<long> batchSequenceNumbers) in batches)
                {
                    await using (eventBatch)
                    {
                        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        sendCts.CancelAfter(TimeSpan.FromSeconds(_options.EventHubSendTimeoutSeconds));

                        try
                        {
                            await producer.SendAsync(eventBatch, sendCts.Token).ConfigureAwait(false);
                            published.AddRange(batchSequenceNumbers);
                            RecordSendSuccess(topicName);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            _lastPublishError = $"[{topicName}/{partitionKey}] EventHub send timeout";
                            OnError($"EventHub send timeout for topic '{topicName}' key '{partitionKey}'", null);
                            RecordSendFailure(topicName);
                        }
                    }
                }
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

    /// <summary>
    /// Builds a list of EventDataBatch objects from the outbox rows, splitting
    /// at the 1 MB EventHub batch size limit.
    /// Returns each batch paired with the sequence numbers it contains.
    /// Rows that are individually too large for any EventHub batch are dead-lettered
    /// immediately rather than being left to cycle through recovery indefinitely.
    /// </summary>
    private async Task<List<(EventDataBatch Batch, List<long> SequenceNumbers)>> BuildEventHubBatchesAsync(
        EventHubProducerClient producer,
        string partitionKey,
        IEnumerable<OutboxRow> rows,
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
        finally
        {
            currentBatch?.Dispose();
        }

        return result;
    }

    private static EventData BuildEventData(OutboxRow row)
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
            catch
            {
                // Malformed headers: skip header propagation rather than failing the message.
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
                // Half-open: allow one attempt
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

    private static DataTable BuildSequenceNumberTvp(IEnumerable<long> sequenceNumbers)
    {
        var dt = new DataTable();
        dt.Columns.Add("SequenceNumber", typeof(long));
        foreach (var sn in sequenceNumbers)
            dt.Rows.Add(sn);
        return dt;
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(milliseconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation so the calling loop exits promptly.
            throw;
        }
    }

    /// <summary>
    /// Override in a subclass or replace with a logging framework call.
    /// Default implementation writes to Console.Error.
    /// </summary>
    protected virtual void OnError(string context, Exception? exception)
    {
        Console.Error.WriteLine($"[OutboxPublisher] {context}: {exception?.Message}");
    }
}

// =============================================================================
// Configuration
// =============================================================================

/// <summary>
/// All tuning parameters for <see cref="OutboxPublisher"/>.
/// Defaults are suitable for most workloads; adjust based on profiling.
/// </summary>
public sealed class OutboxPublisherOptions
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
    /// After this many recovery-path re-leases without success, the row is
    /// moved to dbo.OutboxDeadLetter. Default: 5.
    /// </summary>
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>Minimum adaptive poll interval in milliseconds. Default: 100.</summary>
    public int MinPollIntervalMs { get; set; } = 100;

    /// <summary>Maximum adaptive poll interval (idle backoff ceiling) in milliseconds. Default: 5000.</summary>
    public int MaxPollIntervalMs { get; set; } = 5000;

    /// <summary>Fixed interval for the recovery (expired-lease) poll in milliseconds. Default: 30 000.</summary>
    public int RecoveryPollIntervalMs { get; set; } = 30_000;

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
    /// While open, the publisher pauses polling to avoid burning retry counts.
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
}

// =============================================================================
// Data models
// =============================================================================

/// <summary>A single row read from dbo.Outbox.</summary>
internal sealed record OutboxRow(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload);
