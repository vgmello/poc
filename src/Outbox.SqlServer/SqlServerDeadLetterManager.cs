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
        IOptionsMonitor<SqlServerStoreOptions> optionsMonitor,
        string? groupName = null)
    {
        _options = optionsMonitor.Get(groupName ?? Microsoft.Extensions.Options.Options.DefaultName);
        _db = new SqlServerDbHelper(connectionFactory, serviceProvider, _options);
        _queries = new SqlServerQueries(
            _options.SchemaName, _options.TablePrefix,
            _options.GetSharedSchemaName(), _options.GetOutboxTableName());

        DapperConfiguration.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // Get
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct)
    {
        var messages = await _db.QueryAsync<DeadLetteredMessage>(_queries.DeadLetterGet,
            new { Offset = offset, Limit = limit }, ct).ConfigureAwait(false);

        return messages.AsList();
    }

    // -------------------------------------------------------------------------
    // Replay — move rows back to Outbox for reprocessing
    // -------------------------------------------------------------------------

    public async Task ReplayAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        if (deadLetterSeqs.Count == 0) return;

        var parameters = new DynamicParameters();
        parameters.Add("@Ids",
            SqlServerDbHelper.CreateSequenceNumberTable(deadLetterSeqs).AsTableValuedParameter(_queries.TvpType));
        await _db.ExecuteAsync(_queries.DeadLetterReplay, parameters, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Purge
    // -------------------------------------------------------------------------

    public async Task PurgeAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        if (deadLetterSeqs.Count == 0) return;

        var parameters = new DynamicParameters();
        parameters.Add("@Ids",
            SqlServerDbHelper.CreateSequenceNumberTable(deadLetterSeqs).AsTableValuedParameter(_queries.TvpType));
        await _db.ExecuteAsync(_queries.DeadLetterPurge, parameters, ct).ConfigureAwait(false);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        await _db.ExecuteAsync(_queries.DeadLetterPurgeAll, null, ct).ConfigureAwait(false);
    }
}
