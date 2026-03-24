// Copyright (c) OrgName. All rights reserved.

using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Options;

namespace Outbox.SqlServer;

/// <summary>
///     SQL Server implementation of <see cref="IOutboxStore" />.
/// </summary>
public sealed class SqlServerOutboxStore : IOutboxStore
{
    private readonly SqlServerDbHelper _db;
    private readonly SqlServerStoreOptions _options;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _publisherOptions;
    private readonly SqlServerQueries _queries;

    private volatile int _cachedPartitionCount;
    private long _partitionCountRefreshedAtTicks;

    public SqlServerOutboxStore(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<SqlServerStoreOptions> options,
        IOptionsMonitor<OutboxPublisherOptions> publisherOptions)
    {
        _options = options.Value;
        _publisherOptions = publisherOptions;
        _db = new SqlServerDbHelper(connectionFactory, serviceProvider, _options);
        _queries = new SqlServerQueries(_options.SchemaName, _options.TablePrefix);

        DapperConfiguration.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Producer registration
    // -------------------------------------------------------------------------

    public async Task<string> RegisterProducerAsync(CancellationToken ct)
    {
        var opts = _publisherOptions.CurrentValue;
        var producerId = $"{opts.PublisherName}-{Guid.NewGuid():N}";
        var hostName = Environment.MachineName;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(_queries.RegisterProducer,
                new { ProducerId = producerId, HostName = hostName },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return producerId;
    }

    public async Task UnregisterProducerAsync(string producerId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.UnregisterProducer,
                new { ProducerId = producerId },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
            await tx.CommitAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Lease batch (unified poll)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        string producerId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct)
    {
        var totalPartitions = await GetCachedPartitionCountAsync(ct).ConfigureAwait(false);

        if (totalPartitions == 0)
            return Array.Empty<OutboxMessage>();

        IEnumerable<OutboxMessage>? rows = null;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            rows = await conn.QueryAsync<OutboxMessage>(new CommandDefinition(_queries.LeaseBatch,
                new
                {
                    BatchSize = batchSize,
                    LeaseDurationSeconds = leaseDurationSeconds,
                    PublisherId = producerId,
                    TotalPartitions = totalPartitions,
                    MaxRetryCount = maxRetryCount
                },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return rows?.AsList() ?? [];
    }

    // -------------------------------------------------------------------------
    // Delete / Release / Dead-letter
    // -------------------------------------------------------------------------

    public async Task DeletePublishedAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            var parameters = new DynamicParameters(new { PublisherId = producerId });
            parameters.Add("@PublishedIds",
                SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers).AsTableValuedParameter(_queries.TvpType));
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeletePublished, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers,
        bool incrementRetry, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        var sql = incrementRetry ? _queries.ReleaseLeaseWithRetry : _queries.ReleaseLeaseNoRetry;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            var parameters = new DynamicParameters(new { PublisherId = producerId });
            parameters.Add("@Ids",
                SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers).AsTableValuedParameter(_queries.TvpType));
            await conn.ExecuteAsync(new CommandDefinition(sql, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            var parameters = new DynamicParameters(new { PublisherId = producerId });
            parameters.Add("@LastError", lastError, DbType.String, size: 2000);
            parameters.Add("@Ids",
                SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers).AsTableValuedParameter(_queries.TvpType));
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetter, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    public async Task HeartbeatAsync(string producerId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.Heartbeat,
                new { ProducerId = producerId },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
            await tx.CommitAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Partition management
    // -------------------------------------------------------------------------

    private async Task<int> GetCachedPartitionCountAsync(CancellationToken ct)
    {
        const long refreshIntervalMs = 60_000; // 60s
        var now = Environment.TickCount64;
        var cached = _cachedPartitionCount;

        if (cached > 0 && now - Volatile.Read(ref _partitionCountRefreshedAtTicks) < refreshIntervalMs)
            return cached;

        var fresh = await GetTotalPartitionsAsync(ct).ConfigureAwait(false);
        _cachedPartitionCount = fresh;
        Volatile.Write(ref _partitionCountRefreshedAtTicks, now);

        return fresh;
    }

    public async Task<int> GetTotalPartitionsAsync(CancellationToken ct)
    {
        var result = 0;
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            result = await conn.ExecuteScalarAsync<int>(new CommandDefinition(_queries.GetTotalPartitions,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return result;
    }

    public async Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string producerId, CancellationToken ct)
    {
        IEnumerable<int>? partitions = null;
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            partitions = await conn.QueryAsync<int>(new CommandDefinition(_queries.GetOwnedPartitions,
                new { ProducerId = producerId },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return partitions?.AsList() ?? [];
    }

    public async Task RebalanceAsync(string producerId, CancellationToken ct)
    {
        // Snapshot options once to ensure consistent values across all steps within one transaction.
        var opts = _publisherOptions.CurrentValue;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.Rebalance,
                new
                {
                    ProducerId = producerId,
                    HeartbeatTimeoutSeconds = opts.HeartbeatTimeoutSeconds,
                    PartitionGracePeriodSeconds = opts.PartitionGracePeriodSeconds
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
            await tx.CommitAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ClaimOrphanPartitionsAsync(string producerId, CancellationToken ct)
    {
        var opts = _publisherOptions.CurrentValue;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.ClaimOrphanPartitions,
                new
                {
                    ProducerId = producerId,
                    HeartbeatTimeoutSeconds = opts.HeartbeatTimeoutSeconds
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
            await tx.CommitAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Dead-letter sweep
    // -------------------------------------------------------------------------

    public async Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct)
    {
        var opts = _publisherOptions.CurrentValue;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            var parameters = new DynamicParameters(new
            {
                MaxRetryCount = maxRetryCount,
                HeartbeatTimeoutSeconds = opts.HeartbeatTimeoutSeconds,
                LeaseDurationSeconds = opts.LeaseDurationSeconds
            });
            parameters.Add("@LastError", "Max retry count exceeded (background sweep)", DbType.String, size: 2000);
            await conn.ExecuteAsync(new CommandDefinition(_queries.SweepDeadLetters, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct)
    {
        long result = 0;
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            result = await conn.ExecuteScalarAsync<long>(new CommandDefinition(_queries.GetPendingCount,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return result;
    }
}
