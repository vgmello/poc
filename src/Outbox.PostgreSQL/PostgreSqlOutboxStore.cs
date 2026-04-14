// Copyright (c) OrgName. All rights reserved.

using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Options;

namespace Outbox.PostgreSQL;

public sealed class PostgreSqlOutboxStore : IOutboxStore
{
    private readonly PostgreSqlDbHelper _db;
    private readonly PostgreSqlStoreOptions _options;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _publisherOptions;
    private readonly PostgreSqlQueries _queries;

    private readonly string _optionsName;

    // Partition count only changes during a deliberate schema reconfiguration.
    // Cached with a 60s TTL so FetchBatch (which parameterizes its query with the
    // count) and the publisher service's worker assignment both avoid round-tripping
    // the database on every poll.
    private const long PartitionCountRefreshIntervalMs = 60_000;
    private int _cachedPartitionCount;
    private long _partitionCountRefreshedAtTicks;

    public string PublisherId { get; }

    public PostgreSqlOutboxStore(
        IServiceProvider serviceProvider,
        IOptionsMonitor<PostgreSqlStoreOptions> optionsMonitor,
        IOptionsMonitor<OutboxPublisherOptions> publisherOptions,
        string? groupName = null)
    {
        _optionsName = groupName ?? Options.DefaultName;
        _options = optionsMonitor.Get(_optionsName);
        _publisherOptions = publisherOptions;
        _db = new PostgreSqlDbHelper(serviceProvider, _options);
        _queries = new PostgreSqlQueries(
            _options.SchemaName, _options.TablePrefix,
            _options.GetSharedSchemaName(), _options.GetOutboxTableName());
        DapperConfiguration.EnsureInitialized();

        var publisherName = _publisherOptions.Get(_optionsName).PublisherName;
        PublisherId = _options.GroupName is not null
            ? $"{_options.GroupName}-{publisherName}-{Guid.NewGuid():N}"
            : $"{publisherName}-{Guid.NewGuid():N}";
    }

    // -------------------------------------------------------------------------
    // Publisher lifecycle
    // -------------------------------------------------------------------------

    public async Task<string> RegisterPublisherAsync(CancellationToken ct)
    {
        var hostName = Environment.MachineName;

        await _db.ExecuteAsync(_queries.RegisterPublisher,
            new { publisher_id = PublisherId, host_name = hostName, outbox_table_name = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);

        return PublisherId;
    }

    public async Task UnregisterPublisherAsync(string publisherId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.UnregisterPublisher,
                new { publisher_id = publisherId, outbox_table_name = _options.GetOutboxTableName() },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Fetch and publish operations
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize, CancellationToken ct)
    {
        var totalPartitions = await GetTotalPartitionsAsync(ct).ConfigureAwait(false);

        if (totalPartitions == 0)
            return Array.Empty<OutboxMessage>();

        var rows = await _db.QueryAsync<FetchBatchOutputRow>(_queries.FetchBatch,
            new
            {
                batch_size = batchSize,
                publisher_id = publisherId,
                total_partitions = totalPartitions,
                outbox_table_name = _options.GetOutboxTableName()
            }, ct).ConfigureAwait(false);

        var list = rows.AsList();
        var result = new List<OutboxMessage>(list.Count);
        for (var i = 0; i < list.Count; i++)
            result.Add(list[i].ToDomain());

        return result;
    }

    public async Task DeletePublishedAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
        await _db.ExecuteAsync(_queries.DeletePublished, parameters, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers,
        int attemptCount,
        string? lastError,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
        parameters.Add("@attempt_count", attemptCount, DbType.Int32);
        parameters.Add("@last_error", lastError, DbType.String, size: 2000);
        await _db.ExecuteAsync(_queries.DeadLetter, parameters, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Heartbeat and partition management
    // -------------------------------------------------------------------------

    public async Task HeartbeatAsync(string publisherId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.Heartbeat,
                new { publisher_id = publisherId, outbox_table_name = _options.GetOutboxTableName() },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> GetTotalPartitionsAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        var cached = Volatile.Read(ref _cachedPartitionCount);

        if (cached > 0 && now - Volatile.Read(ref _partitionCountRefreshedAtTicks) < PartitionCountRefreshIntervalMs)
            return cached;

        var fresh = await _db.ScalarAsync<int>(_queries.GetTotalPartitions,
            new { outbox_table_name = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);

        Volatile.Write(ref _cachedPartitionCount, fresh);
        Volatile.Write(ref _partitionCountRefreshedAtTicks, now);

        return fresh;
    }

    public async Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct)
    {
        var partitions = await _db.QueryAsync<int>(_queries.GetOwnedPartitions,
            new { publisher_id = publisherId, outbox_table_name = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);

        return partitions.AsList();
    }

    public async Task RebalanceAsync(string publisherId, CancellationToken ct)
    {
        var opts = _publisherOptions.Get(_optionsName);

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);

            // Mark stale, claim, and release pipelined in a single command.
            await conn.ExecuteAsync(new CommandDefinition(_queries.Rebalance,
                new
                {
                    publisher_id = publisherId,
                    heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds,
                    partition_grace_period_seconds = (double)opts.PartitionGracePeriodSeconds,
                    outbox_table_name = _options.GetOutboxTableName()
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);

            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct)
    {
        var opts = _publisherOptions.Get(_optionsName);

        await _db.ExecuteAsync(_queries.ClaimOrphanPartitions,
            new
            {
                publisher_id = publisherId,
                heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds,
                outbox_table_name = _options.GetOutboxTableName()
            }, ct).ConfigureAwait(false);
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct)
    {
        return await _db.ScalarAsync<long>(_queries.GetPendingCount, null, ct).ConfigureAwait(false);
    }
}
