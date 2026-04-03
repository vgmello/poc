// Copyright (c) OrgName. All rights reserved.

using System.Data;
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
    public SqlServerOutboxStore(
        IServiceProvider serviceProvider,
        IOptionsMonitor<SqlServerStoreOptions> optionsMonitor,
        IOptionsMonitor<OutboxPublisherOptions> publisherOptions,
        string? groupName = null)
    {
        _optionsName = groupName ?? Microsoft.Extensions.Options.Options.DefaultName;
        _options = optionsMonitor.Get(_optionsName);
        _publisherOptions = publisherOptions;
        _db = new SqlServerDbHelper(serviceProvider, _options);
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
    // Fetch batch (unified poll)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize,
        int maxRetryCount, CancellationToken ct)
    {
        var rows = await _db.QueryAsync<OutboxMessage>(_queries.FetchBatch,
            new
            {
                BatchSize = batchSize,
                PublisherId = publisherId,
                MaxRetryCount = maxRetryCount,
                OutboxTableName = _options.GetOutboxTableName()
            }, ct).ConfigureAwait(false);

        return rows.AsList();
    }

    // -------------------------------------------------------------------------
    // Delete / Dead-letter / IncrementRetry
    // -------------------------------------------------------------------------

    public async Task DeletePublishedAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        var parameters = new DynamicParameters();
        parameters.Add("@Ids",
            SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers).AsTableValuedParameter(_queries.TvpType));
        await _db.ExecuteAsync(_queries.DeletePublished, parameters, ct).ConfigureAwait(false);
    }

    public async Task IncrementRetryCountAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            var dt = SqlServerDbHelper.CreateSequenceNumberTable(sequenceNumbers);
            var parameters = new DynamicParameters();
            parameters.Add("@Ids", dt.AsTableValuedParameter(_queries.TvpType));
            await conn.ExecuteAsync(new CommandDefinition(
                _queries.IncrementRetryCount, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;

        var parameters = new DynamicParameters();
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

    public async Task SweepDeadLettersAsync(string publisherId, int maxRetryCount, CancellationToken ct)
    {
        var parameters = new DynamicParameters(new { PublisherId = publisherId, MaxRetryCount = maxRetryCount, OutboxTableName = _options.GetOutboxTableName() });
        parameters.Add("@LastError", "Max retry count exceeded (background sweep)", DbType.String, size: 2000);
        await _db.ExecuteAsync(_queries.SweepDeadLetters, parameters, ct).ConfigureAwait(false);
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct)
    {
        return await _db.ScalarAsync<long>(_queries.GetPendingCount, null, ct).ConfigureAwait(false);
    }
}
