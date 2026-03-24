// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.SqlServer;

/// <summary>
///     SQL Server implementation of <see cref="IDeadLetterManager" />.
/// </summary>
public sealed class SqlServerDeadLetterManager : IDeadLetterManager
{
    private readonly SqlServerDbHelper _db;
    private readonly SqlServerStoreOptions _options;
    private readonly SqlServerQueries _queries;

    public SqlServerDeadLetterManager(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<SqlServerStoreOptions> options)
    {
        _options = options.Value;
        _db = new SqlServerDbHelper(connectionFactory, serviceProvider, _options);
        _queries = new SqlServerQueries(_options.SchemaName, _options.TablePrefix);

        DapperConfiguration.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Get
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct)
    {
        IEnumerable<DeadLetteredMessage>? messages = null;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            messages = await conn.QueryAsync<DeadLetteredMessage>(new CommandDefinition(_queries.DeadLetterGet,
                new { Offset = offset, Limit = limit },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return messages?.AsList() ?? [];
    }

    // -------------------------------------------------------------------------
    // Replay — move rows back to Outbox for reprocessing
    // -------------------------------------------------------------------------

    public async Task ReplayAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        if (deadLetterSeqs.Count == 0) return;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            var parameters = new DynamicParameters();
            parameters.Add("@Ids",
                SqlServerDbHelper.CreateSequenceNumberTable(deadLetterSeqs).AsTableValuedParameter(_queries.TvpType));
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetterReplay, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Purge
    // -------------------------------------------------------------------------

    public async Task PurgeAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        if (deadLetterSeqs.Count == 0) return;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            var parameters = new DynamicParameters();
            parameters.Add("@Ids",
                SqlServerDbHelper.CreateSequenceNumberTable(deadLetterSeqs).AsTableValuedParameter(_queries.TvpType));
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetterPurge, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetterPurgeAll,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
