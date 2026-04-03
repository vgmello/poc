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
    private int _cachedPartitionCount;
    private long _partitionCountRefreshedAtTicks;

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
    }

    // -------------------------------------------------------------------------
    // Publisher lifecycle
    // -------------------------------------------------------------------------

    public async Task<string> RegisterPublisherAsync(CancellationToken ct)
    {
        var opts = _publisherOptions.Get(_optionsName);
        var publisherId = _options.GroupName is not null
            ? $"{_options.GroupName}-{opts.PublisherName}-{Guid.NewGuid():N}"
            : $"{opts.PublisherName}-{Guid.NewGuid():N}";
        var hostName = Environment.MachineName;

        await _db.ExecuteAsync(_queries.RegisterPublisher,
            new { publisher_id = publisherId, host_name = hostName, outbox_table_name = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);

        return publisherId;
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
        string publisherId, int batchSize,
        int maxRetryCount, CancellationToken ct)
    {
        var totalPartitions = await GetCachedPartitionCountAsync(ct).ConfigureAwait(false);

        if (totalPartitions == 0)
            return Array.Empty<OutboxMessage>();

        var rows = await _db.QueryAsync<OutboxMessage>(_queries.FetchBatch,
            new
            {
                batch_size = batchSize,
                publisher_id = publisherId,
                total_partitions = totalPartitions,
                max_retry_count = maxRetryCount,
                outbox_table_name = _options.GetOutboxTableName()
            }, ct).ConfigureAwait(false);

        return rows.AsList();
    }

    public async Task DeletePublishedAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
        await _db.ExecuteAsync(_queries.DeletePublished, parameters, ct).ConfigureAwait(false);
    }

    public async Task IncrementRetryCountAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
        await _db.ExecuteAsync(_queries.IncrementRetryCount, parameters, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
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

    private async Task<int> GetCachedPartitionCountAsync(CancellationToken ct)
    {
        const long refreshIntervalMs = 60_000; // 60s
        var now = Environment.TickCount64;
        var cached = Volatile.Read(ref _cachedPartitionCount);

        if (cached > 0 && now - Volatile.Read(ref _partitionCountRefreshedAtTicks) < refreshIntervalMs)
            return cached;

        var fresh = await GetTotalPartitionsAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _cachedPartitionCount, fresh);
        Volatile.Write(ref _partitionCountRefreshedAtTicks, now);

        return fresh;
    }

    public async Task<int> GetTotalPartitionsAsync(CancellationToken ct)
    {
        return await _db.ScalarAsync<int>(_queries.GetTotalPartitions,
            new { outbox_table_name = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct)
    {
        var partitions = await _db.QueryAsync<int>(_queries.GetOwnedPartitions,
            new { publisher_id = publisherId, outbox_table_name = _options.GetOutboxTableName() }, ct).ConfigureAwait(false);

        return partitions.AsList();
    }

    public async Task RebalanceAsync(string publisherId, CancellationToken ct)
    {
        // Snapshot options once to ensure consistent values across all steps within one transaction.
        var opts = _publisherOptions.Get(_optionsName);

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);

            // Step 1: Mark stale publishers' partitions as entering grace period
            await conn.ExecuteAsync(new CommandDefinition(_queries.RebalanceMarkStale,
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

            // Step 2: Calculate fair share and claim unowned/grace-expired partitions
            await conn.ExecuteAsync(new CommandDefinition(_queries.RebalanceClaim,
                new
                {
                    publisher_id = publisherId,
                    heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds,
                    outbox_table_name = _options.GetOutboxTableName()
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);

            // Step 3: Release excess above fair share
            await conn.ExecuteAsync(new CommandDefinition(_queries.RebalanceRelease,
                new
                {
                    publisher_id = publisherId,
                    heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds,
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

    public async Task SweepDeadLettersAsync(string publisherId, int maxRetryCount, CancellationToken ct)
    {
        var parameters = new DynamicParameters(new { publisher_id = publisherId, max_retry_count = maxRetryCount, outbox_table_name = _options.GetOutboxTableName() });
        parameters.Add("@last_error", "Max retry count exceeded (background sweep)", DbType.String, size: 2000);
        await _db.ExecuteAsync(_queries.SweepDeadLetters, parameters, ct).ConfigureAwait(false);
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct)
    {
        return await _db.ScalarAsync<long>(_queries.GetPendingCount, null, ct).ConfigureAwait(false);
    }
}
