using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Options;

namespace Outbox.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IOutboxStore"/>.
/// </summary>
public sealed class SqlServerOutboxStore : IOutboxStore
{
    private readonly Func<IServiceProvider, CancellationToken, Task<DbConnection>> _connectionFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly SqlServerStoreOptions _options;
    private readonly OutboxPublisherOptions _publisherOptions;

    public SqlServerOutboxStore(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<SqlServerStoreOptions> options,
        IOptions<OutboxPublisherOptions> publisherOptions)
    {
        _connectionFactory = connectionFactory;
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _publisherOptions = publisherOptions.Value;
    }

    // -------------------------------------------------------------------------
    // Producer registration
    // -------------------------------------------------------------------------

    public async Task<string> RegisterProducerAsync(CancellationToken ct)
    {
        var producerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var hostName = Environment.MachineName;
        var schema = _options.SchemaName;

        var sql = $"""
            MERGE {schema}.OutboxProducers WITH (HOLDLOCK) AS target
            USING (SELECT @ProducerId AS ProducerId, @HostName AS HostName) AS source
                ON target.ProducerId = source.ProducerId
            WHEN MATCHED THEN
                UPDATE SET LastHeartbeatUtc = SYSUTCDATETIME(),
                           HostName         = source.HostName
            WHEN NOT MATCHED THEN
                INSERT (ProducerId, RegisteredAtUtc, LastHeartbeatUtc, HostName)
                VALUES (source.ProducerId, SYSUTCDATETIME(), SYSUTCDATETIME(), source.HostName);
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", producerId);
            cmd.Parameters.AddWithValue("@HostName", hostName);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return producerId;
    }

    public async Task UnregisterProducerAsync(string producerId, CancellationToken ct)
    {
        var schema = _options.SchemaName;

        var releaseSql = $"""
            UPDATE {schema}.OutboxPartitions
            SET    OwnerProducerId = NULL,
                   OwnedSinceUtc  = NULL,
                   GraceExpiresUtc = NULL
            WHERE  OwnerProducerId = @ProducerId;
            """;

        var deleteSql = $"""
            DELETE FROM {schema}.OutboxProducers
            WHERE  ProducerId = @ProducerId;
            """;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var releaseCmd = (SqlCommand)conn.CreateCommand())
        {
            releaseCmd.Transaction = tx;
            releaseCmd.CommandText = releaseSql;
            releaseCmd.CommandTimeout = _options.CommandTimeoutSeconds;
            releaseCmd.Parameters.AddWithValue("@ProducerId", producerId);
            await releaseCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var deleteCmd = (SqlCommand)conn.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = deleteSql;
            deleteCmd.CommandTimeout = _options.CommandTimeoutSeconds;
            deleteCmd.Parameters.AddWithValue("@ProducerId", producerId);
            await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Lease batch (unified poll)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        string producerId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct)
    {
        var schema = _options.SchemaName;

        // We need the total partition count to compute the partition key hash.
        // The engine already caches this; here we read it per-call.
        int totalPartitions = await GetTotalPartitionsAsync(ct).ConfigureAwait(false);
        if (totalPartitions == 0)
            return Array.Empty<OutboxMessage>();

        var sql = $"""
            WITH Batch AS
            (
                SELECT TOP (@BatchSize)
                    o.SequenceNumber,
                    o.TopicName,
                    o.PartitionKey,
                    o.EventType,
                    o.Headers,
                    o.Payload,
                    o.EventDateTimeUtc,
                    o.EventOrdinal,
                    o.LeasedUntilUtc,
                    o.LeaseOwner,
                    o.RetryCount,
                    o.CreatedAtUtc
                FROM {schema}.Outbox o WITH (ROWLOCK, READPAST)
                INNER JOIN {schema}.OutboxPartitions op
                    ON  op.OwnerProducerId = @PublisherId
                    AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
                    AND (ABS(CHECKSUM(o.PartitionKey)) % @TotalPartitions) = op.PartitionId
                WHERE (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME())
                  AND o.RetryCount < @MaxRetryCount
                ORDER BY o.EventDateTimeUtc, o.EventOrdinal
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
                   inserted.EventDateTimeUtc,
                   inserted.EventOrdinal,
                   inserted.RetryCount,
                   inserted.CreatedAtUtc;
            """;

        var rows = new List<OutboxMessage>();

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            rows.Clear();
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@BatchSize", batchSize);
            cmd.Parameters.AddWithValue("@LeaseDurationSeconds", leaseDurationSeconds);
            cmd.Parameters.AddWithValue("@PublisherId", producerId);
            cmd.Parameters.AddWithValue("@TotalPartitions", totalPartitions);
            cmd.Parameters.AddWithValue("@MaxRetryCount", maxRetryCount);

            await using var reader = await cmd.ExecuteReaderAsync(cancel).ConfigureAwait(false);
            while (await reader.ReadAsync(cancel).ConfigureAwait(false))
            {
                rows.Add(new OutboxMessage(
                    SequenceNumber: reader.GetInt64(0),
                    TopicName: reader.GetString(1),
                    PartitionKey: reader.GetString(2),
                    EventType: reader.GetString(3),
                    Headers: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Payload: reader.GetString(5),
                    EventDateTimeUtc: reader.GetDateTime(6),
                    EventOrdinal: reader.GetInt16(7),
                    RetryCount: reader.GetInt32(8),
                    CreatedAtUtc: reader.GetDateTime(9)));
            }
        }, ct).ConfigureAwait(false);

        return rows;
    }

    // -------------------------------------------------------------------------
    // Delete / Release / Dead-letter
    // -------------------------------------------------------------------------

    public async Task DeletePublishedAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;
        var schema = _options.SchemaName;

        var sql = $"""
            DELETE o
            FROM   {schema}.Outbox o
            INNER JOIN @PublishedIds p ON o.SequenceNumber = p.SequenceNumber
            WHERE  o.LeaseOwner = @PublisherId;
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@PublisherId", producerId);
            AddSequenceNumberTvp(cmd, "@PublishedIds", sequenceNumbers, schema);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;
        var schema = _options.SchemaName;

        var sql = $"""
            UPDATE o
            SET    o.LeasedUntilUtc = NULL,
                   o.LeaseOwner     = NULL
            FROM   {schema}.Outbox o
            INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
            WHERE  o.LeaseOwner = @PublisherId;
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@PublisherId", producerId);
            AddSequenceNumberTvp(cmd, "@Ids", sequenceNumbers, schema);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;
        var schema = _options.SchemaName;

        // Dead-letter specific sequence numbers (by ID list), not by RetryCount threshold.
        // Use DELETE...OUTPUT INTO for atomicity.
        var sql = $"""
            DELETE o
            OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
                   deleted.EventType, deleted.Headers, deleted.Payload,
                   deleted.CreatedAtUtc, deleted.RetryCount,
                   deleted.EventDateTimeUtc, deleted.EventOrdinal,
                   SYSUTCDATETIME(), @LastError
            INTO {schema}.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
                 Headers, Payload, CreatedAtUtc, RetryCount,
                 EventDateTimeUtc, EventOrdinal,
                 DeadLetteredAtUtc, LastError)
            FROM {schema}.Outbox o
            INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber;
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
                (object?)lastError ?? DBNull.Value;
            AddSequenceNumberTvp(cmd, "@Ids", sequenceNumbers, schema);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    public async Task HeartbeatAsync(string producerId, CancellationToken ct)
    {
        var schema = _options.SchemaName;

        var sql = $"""
            UPDATE {schema}.OutboxProducers
            SET    LastHeartbeatUtc = SYSUTCDATETIME()
            WHERE  ProducerId = @ProducerId;

            UPDATE {schema}.OutboxPartitions
            SET    GraceExpiresUtc = NULL
            WHERE  OwnerProducerId = @ProducerId
              AND  GraceExpiresUtc IS NOT NULL;
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", producerId);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Partition management
    // -------------------------------------------------------------------------

    public async Task<int> GetTotalPartitionsAsync(CancellationToken ct)
    {
        var schema = _options.SchemaName;
        var sql = $"SELECT COUNT(*) FROM {schema}.OutboxPartitions;";

        int result = 0;
        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            var scalar = await cmd.ExecuteScalarAsync(cancel).ConfigureAwait(false);
            result = Convert.ToInt32(scalar);
        }, ct).ConfigureAwait(false);

        return result;
    }

    public async Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string producerId, CancellationToken ct)
    {
        var schema = _options.SchemaName;
        var sql = $"""
            SELECT PartitionId
            FROM   {schema}.OutboxPartitions
            WHERE  OwnerProducerId = @ProducerId;
            """;

        var partitions = new List<int>();
        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            partitions.Clear();
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", producerId);
            await using var reader = await cmd.ExecuteReaderAsync(cancel).ConfigureAwait(false);
            while (await reader.ReadAsync(cancel).ConfigureAwait(false))
                partitions.Add(reader.GetInt32(0));
        }, ct).ConfigureAwait(false);

        return partitions;
    }

    public async Task RebalanceAsync(string producerId, CancellationToken ct)
    {
        var schema = _options.SchemaName;

        var sql = $"""
            BEGIN TRANSACTION;

            DECLARE @TotalPartitions   INT;
            DECLARE @ActiveProducers   INT;
            DECLARE @FairShare         INT;
            DECLARE @CurrentlyOwned    INT;
            DECLARE @ToAcquire         INT;

            SELECT @TotalPartitions = COUNT(*) FROM {schema}.OutboxPartitions;

            SELECT @ActiveProducers = COUNT(*)
            FROM {schema}.OutboxProducers
            WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

            SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

            SELECT @CurrentlyOwned = COUNT(*)
            FROM {schema}.OutboxPartitions
            WHERE OwnerProducerId = @ProducerId;

            SET @ToAcquire = @FairShare - @CurrentlyOwned;

            IF @ToAcquire > 0
            BEGIN
                UPDATE {schema}.OutboxPartitions
                SET    GraceExpiresUtc = DATEADD(SECOND, @PartitionGracePeriodSeconds, SYSUTCDATETIME())
                WHERE  OwnerProducerId <> @ProducerId
                  AND  OwnerProducerId IS NOT NULL
                  AND  GraceExpiresUtc IS NULL
                  AND  OwnerProducerId NOT IN
                       (
                           SELECT ProducerId
                           FROM   {schema}.OutboxProducers
                           WHERE  LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
                       );

                UPDATE TOP (@ToAcquire) {schema}.OutboxPartitions WITH (UPDLOCK)
                SET    OwnerProducerId = @ProducerId,
                       OwnedSinceUtc   = SYSUTCDATETIME(),
                       GraceExpiresUtc = NULL
                WHERE  (OwnerProducerId IS NULL
                        OR GraceExpiresUtc < SYSUTCDATETIME());
            END;

            SELECT @CurrentlyOwned = COUNT(*)
            FROM {schema}.OutboxPartitions
            WHERE OwnerProducerId = @ProducerId;

            IF @CurrentlyOwned > @FairShare
            BEGIN
                DECLARE @ToRelease INT = @CurrentlyOwned - @FairShare;

                UPDATE TOP (@ToRelease) {schema}.OutboxPartitions
                SET    OwnerProducerId = NULL,
                       OwnedSinceUtc  = NULL,
                       GraceExpiresUtc = NULL
                WHERE  OwnerProducerId = @ProducerId;
            END;

            COMMIT TRANSACTION;
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", producerId);
            cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _publisherOptions.HeartbeatTimeoutSeconds);
            cmd.Parameters.AddWithValue("@PartitionGracePeriodSeconds", _publisherOptions.PartitionGracePeriodSeconds);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ClaimOrphanPartitionsAsync(string producerId, CancellationToken ct)
    {
        var schema = _options.SchemaName;

        var sql = $"""
            DECLARE @TotalPartitions   INT;
            DECLARE @ActiveProducers   INT;
            DECLARE @FairShare         INT;
            DECLARE @CurrentlyOwned    INT;
            DECLARE @ToAcquire         INT;

            SELECT @TotalPartitions = COUNT(*) FROM {schema}.OutboxPartitions;

            SELECT @ActiveProducers = COUNT(*)
            FROM {schema}.OutboxProducers
            WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

            SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

            SELECT @CurrentlyOwned = COUNT(*)
            FROM {schema}.OutboxPartitions
            WHERE OwnerProducerId = @ProducerId;

            SET @ToAcquire = @FairShare - @CurrentlyOwned;

            IF @ToAcquire > 0
            BEGIN
                UPDATE TOP (@ToAcquire) {schema}.OutboxPartitions WITH (UPDLOCK)
                SET    OwnerProducerId = @ProducerId,
                       OwnedSinceUtc   = SYSUTCDATETIME(),
                       GraceExpiresUtc = NULL
                WHERE  OwnerProducerId IS NULL;
            END;
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@ProducerId", producerId);
            cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _publisherOptions.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Dead-letter sweep
    // -------------------------------------------------------------------------

    public async Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct)
    {
        var schema = _options.SchemaName;

        var sql = $"""
            DELETE o
            OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
                   deleted.EventType, deleted.Headers, deleted.Payload,
                   deleted.CreatedAtUtc, deleted.RetryCount,
                   deleted.EventDateTimeUtc, deleted.EventOrdinal,
                   SYSUTCDATETIME(), @LastError
            INTO {schema}.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
                 Headers, Payload, CreatedAtUtc, RetryCount,
                 EventDateTimeUtc, EventOrdinal,
                 DeadLetteredAtUtc, LastError)
            FROM {schema}.Outbox o WITH (ROWLOCK, READPAST)
            WHERE o.RetryCount >= @MaxRetryCount
              AND (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME());
            """;

        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@MaxRetryCount", maxRetryCount);
            cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value = DBNull.Value;
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Infrastructure helpers
    // -------------------------------------------------------------------------

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = (SqlConnection)await _connectionFactory(_serviceProvider, ct).ConfigureAwait(false);
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private async Task ExecuteWithRetryAsync(
        Func<SqlConnection, CancellationToken, Task> operation,
        CancellationToken ct)
    {
        int maxAttempts = _options.TransientRetryMaxAttempts;
        int backoffMs = _options.TransientRetryBackoffMs;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
                await operation(conn, ct).ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (IsTransientSqlError(ex) && attempt < maxAttempts)
            {
                await Task.Delay(backoffMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static void AddSequenceNumberTvp(
        SqlCommand cmd, string paramName, IReadOnlyList<long> sequenceNumbers, string schema)
    {
        var dt = new DataTable();
        dt.Columns.Add("SequenceNumber", typeof(long));
        foreach (var sn in sequenceNumbers)
            dt.Rows.Add(sn);

        var param = cmd.Parameters.AddWithValue(paramName, dt);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName = $"{schema}.SequenceNumberList";
    }

    private static bool IsTransientSqlError(SqlException ex)
        => ex.Number is 1205    // deadlock victim
            or -2               // timeout
            or 40613            // Azure SQL database not available
            or 40197            // Azure SQL service error
            or 40501            // Azure SQL service busy
            or 49918 or 49919 or 49920; // Azure SQL transient errors
}
