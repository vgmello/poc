// Copyright (c) OrgName. All rights reserved.

using System.Data;
using System.Data.Common;
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

    private volatile int _cachedPartitionCount;
    private long _partitionCountRefreshedAtTicks;

    public PostgreSqlOutboxStore(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<PostgreSqlStoreOptions> options,
        IOptionsMonitor<OutboxPublisherOptions> publisherOptions)
    {
        _options = options.Value;
        _publisherOptions = publisherOptions;
        _db = new PostgreSqlDbHelper(connectionFactory, serviceProvider, _options);
        _queries = new PostgreSqlQueries(_options.SchemaName, _options.TablePrefix);
        DapperConfiguration.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Producer lifecycle
    // -------------------------------------------------------------------------

    public async Task<string> RegisterProducerAsync(CancellationToken ct)
    {
        var opts = _publisherOptions.CurrentValue;
        var producerId = $"{opts.PublisherName}-{Guid.NewGuid():N}";
        var hostName = Environment.MachineName;

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(_queries.RegisterProducer,
                new { producer_id = producerId, host_name = hostName },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return producerId;
    }

    public async Task UnregisterProducerAsync(string producerId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.UnregisterProducer,
                new { producer_id = producerId },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
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
        var totalPartitions = await GetCachedPartitionCountAsync(ct).ConfigureAwait(false);

        if (totalPartitions == 0)
            return Array.Empty<OutboxMessage>();

        IEnumerable<OutboxMessage>? rows = null;

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            rows = await conn.QueryAsync<OutboxMessage>(new CommandDefinition(_queries.LeaseBatch,
                new
                {
                    batch_size = batchSize,
                    lease_duration_seconds = (double)leaseDurationSeconds,
                    publisher_id = producerId,
                    total_partitions = totalPartitions,
                    max_retry_count = maxRetryCount
                },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return rows?.AsList() ?? [];
    }

    public async Task DeletePublishedAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            var parameters = new DynamicParameters(new { publisher_id = producerId });
            parameters.Add("@published_ids", new BigintArrayParam(sequenceNumbers));
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeletePublished, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers,
        bool incrementRetry, CancellationToken ct)
    {
        var sql = incrementRetry ? _queries.ReleaseLeaseWithRetry : _queries.ReleaseLeaseNoRetry;

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            var parameters = new DynamicParameters(new { publisher_id = producerId });
            parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
            await conn.ExecuteAsync(new CommandDefinition(sql, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            var parameters = new DynamicParameters(new { publisher_id = producerId });
            parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
            parameters.Add("@last_error", lastError, DbType.String, size: 2000);
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetter, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Heartbeat and partition management
    // -------------------------------------------------------------------------

    public async Task HeartbeatAsync(string producerId, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(_queries.Heartbeat,
                new { producer_id = producerId },
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
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            result = await conn.ExecuteScalarAsync<int>(new CommandDefinition(_queries.GetTotalPartitions,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return result;
    }

    public async Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string producerId, CancellationToken ct)
    {
        IEnumerable<int>? partitions = null;
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            partitions = await conn.QueryAsync<int>(new CommandDefinition(_queries.GetOwnedPartitions,
                new { producer_id = producerId },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return partitions?.AsList() ?? [];
    }

    public async Task RebalanceAsync(string producerId, CancellationToken ct)
    {
        // Snapshot options once to ensure consistent values across all steps within one transaction.
        var opts = _publisherOptions.CurrentValue;

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);

            // Step 1: Mark stale producers' partitions as entering grace period
            await conn.ExecuteAsync(new CommandDefinition(_queries.RebalanceMarkStale,
                new
                {
                    producer_id = producerId,
                    heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds,
                    partition_grace_period_seconds = (double)opts.PartitionGracePeriodSeconds
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);

            // Step 2: Calculate fair share and claim unowned/grace-expired partitions
            await conn.ExecuteAsync(new CommandDefinition(_queries.RebalanceClaim,
                new
                {
                    producer_id = producerId,
                    heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);

            // Step 3: Release excess above fair share
            await conn.ExecuteAsync(new CommandDefinition(_queries.RebalanceRelease,
                new
                {
                    producer_id = producerId,
                    heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds
                },
                transaction: tx,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);

            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ClaimOrphanPartitionsAsync(string producerId, CancellationToken ct)
    {
        var opts = _publisherOptions.CurrentValue;

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(_queries.ClaimOrphanPartitions,
                new
                {
                    producer_id = producerId,
                    heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds
                },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct)
    {
        var opts = _publisherOptions.CurrentValue;
        // Only sweep messages whose lease_owner is NULL (explicitly released),
        // whose owner is a dead producer (stale heartbeat), or whose lease has been
        // expired for longer than LeaseDurationSeconds (publisher had ample time to
        // delete but didn't — covers the case where DeadLetterAsync itself failed).

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            var parameters = new DynamicParameters(new
            {
                max_retry_count = maxRetryCount,
                heartbeat_timeout_seconds = (double)opts.HeartbeatTimeoutSeconds,
                lease_duration_seconds = (double)opts.LeaseDurationSeconds
            });
            parameters.Add("@last_error", "Max retry count exceeded (background sweep)", DbType.String, size: 2000);
            await conn.ExecuteAsync(new CommandDefinition(_queries.SweepDeadLetters, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct)
    {
        long result = 0;
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            result = await conn.ExecuteScalarAsync<long>(new CommandDefinition(_queries.GetPendingCount,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return result;
    }
}
