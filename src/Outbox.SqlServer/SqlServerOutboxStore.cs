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

    private readonly string _optionsName;
    private volatile int _cachedPartitionCount;
    private long _partitionCountRefreshedAtTicks;

    public SqlServerOutboxStore(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptionsMonitor<SqlServerStoreOptions> optionsMonitor,
        IOptionsMonitor<OutboxPublisherOptions> publisherOptions,
        string? groupName = null)
    {
        _optionsName = groupName ?? Microsoft.Extensions.Options.Options.DefaultName;
        _options = optionsMonitor.Get(_optionsName);
        _publisherOptions = publisherOptions;
        _db = new SqlServerDbHelper(connectionFactory, serviceProvider, _options);
        _queries = new SqlServerQueries(
            _options.SchemaName, _options.TablePrefix,
            _options.GetSharedSchemaName(), _options.GetOutboxTableName());

        DapperConfiguration.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Publisher registration
    // -------------------------------------------------------------------------

    public async Task<string> RegisterPublisherAsync(CancellationToken ct)
    {
        var opts = _publisherOptions.Get(_optionsName);
        var publisherId = _options.GroupName is not null
            ? $"{_options.GroupName}-{opts.PublisherName}-{Guid.NewGuid():N}"
            : $"{opts.PublisherName}-{Guid.NewGuid():N}";
        var hostName = Environment.MachineName;

        await _db.ExecuteAsync(_queries.RegisterPublisher,
            new { PublisherId = publisherId, HostName = hostName, OutboxTableName = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);

        return publisherId;
    }

    public async Task UnregisterPublisherAsync(string publisherId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.UnregisterPublisher,
                new { PublisherId = publisherId, OutboxTableName = _options.GetOutboxTableName() },
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
        string publisherId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct)
    {
        var totalPartitions = await GetCachedPartitionCountAsync(ct).ConfigureAwait(false);

        if (totalPartitions == 0)
            return Array.Empty<OutboxMessage>();

        var rows = await _db.QueryAsync<OutboxMessage>(_queries.LeaseBatch,
            new
            {
                BatchSize = batchSize,
                LeaseDurationSeconds = leaseDurationSeconds,
                PublisherId = publisherId,
                TotalPartitions = totalPartitions,
                MaxRetryCount = maxRetryCount,
                OutboxTableName = _options.GetOutboxTableName()
            }, ct).ConfigureAwait(false);

        return rows.AsList();
    }

    // -------------------------------------------------------------------------
    // Delete / Release / Dead-letter
    // -------------------------------------------------------------------------

    public async Task DeletePublishedAsync(
        string publisherId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        var parameters = new DynamicParameters(new { PublisherId = publisherId });
        parameters.Add("@PublishedIds",
            SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers).AsTableValuedParameter(_queries.TvpType));
        await _db.ExecuteAsync(_queries.DeletePublished, parameters, ct).ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(
        string publisherId, IReadOnlyList<long> sequenceNumbers,
        bool incrementRetry, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        var sql = incrementRetry ? _queries.ReleaseLeaseWithRetry : _queries.ReleaseLeaseNoRetry;

        var parameters = new DynamicParameters(new { PublisherId = publisherId });
        parameters.Add("@Ids",
            SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers).AsTableValuedParameter(_queries.TvpType));
        await _db.ExecuteAsync(sql, parameters, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        string publisherId, IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        var parameters = new DynamicParameters(new { PublisherId = publisherId });
        parameters.Add("@LastError", lastError, DbType.String, size: 2000);
        parameters.Add("@Ids",
            SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers).AsTableValuedParameter(_queries.TvpType));
        await _db.ExecuteAsync(_queries.DeadLetter, parameters, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    public async Task HeartbeatAsync(string publisherId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.Heartbeat,
                new { PublisherId = publisherId, OutboxTableName = _options.GetOutboxTableName() },
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
        return await _db.ScalarAsync<int>(_queries.GetTotalPartitions,
            new { OutboxTableName = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct)
    {
        var partitions = await _db.QueryAsync<int>(_queries.GetOwnedPartitions,
            new { PublisherId = publisherId, OutboxTableName = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);

        return partitions.AsList();
    }

    public async Task RebalanceAsync(string publisherId, CancellationToken ct)
    {
        // Snapshot options once to ensure consistent values across all steps within one transaction.
        var opts = _publisherOptions.Get(_optionsName);

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.Rebalance,
                new
                {
                    PublisherId = publisherId,
                    HeartbeatTimeoutSeconds = opts.HeartbeatTimeoutSeconds,
                    PartitionGracePeriodSeconds = opts.PartitionGracePeriodSeconds,
                    OutboxTableName = _options.GetOutboxTableName()
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
            await tx.CommitAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct)
    {
        var opts = _publisherOptions.Get(_optionsName);

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var tx = await conn.BeginTransactionAsync(cancel).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.ClaimOrphanPartitions,
                new
                {
                    PublisherId = publisherId,
                    HeartbeatTimeoutSeconds = opts.HeartbeatTimeoutSeconds,
                    OutboxTableName = _options.GetOutboxTableName()
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
        var opts = _publisherOptions.Get(_optionsName);

        var parameters = new DynamicParameters(new
        {
            MaxRetryCount = maxRetryCount,
            HeartbeatTimeoutSeconds = opts.HeartbeatTimeoutSeconds,
            LeaseDurationSeconds = opts.LeaseDurationSeconds,
            OutboxTableName = _options.GetOutboxTableName()
        });
        parameters.Add("@LastError", "Max retry count exceeded (background sweep)", DbType.String, size: 2000);
        await _db.ExecuteAsync(_queries.SweepDeadLetters, parameters, ct).ConfigureAwait(false);
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct)
    {
        return await _db.ScalarAsync<long>(_queries.GetPendingCount, null, ct).ConfigureAwait(false);
    }
}
