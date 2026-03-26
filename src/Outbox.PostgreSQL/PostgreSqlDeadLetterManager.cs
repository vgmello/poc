// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.PostgreSQL;

public sealed class PostgreSqlDeadLetterManager : IDeadLetterManager
{
    private readonly PostgreSqlDbHelper _db;
    private readonly PostgreSqlStoreOptions _options;
    private readonly PostgreSqlQueries _queries;

    public PostgreSqlDeadLetterManager(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptionsMonitor<PostgreSqlStoreOptions> optionsMonitor,
        string? groupName = null)
    {
        _options = optionsMonitor.Get(groupName ?? Options.DefaultName);
        _db = new PostgreSqlDbHelper(connectionFactory, serviceProvider, _options);
        _queries = new PostgreSqlQueries(
            _options.SchemaName, _options.TablePrefix,
            _options.GetSharedSchemaName(), _options.GetOutboxTableName());
        DapperConfiguration.EnsureInitialized();
    }

    public async Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct)
    {
        var messages = await _db.QueryAsync<DeadLetteredMessage>(_queries.DeadLetterGet,
            new { limit, offset }, ct).ConfigureAwait(false);

        return messages.AsList();
    }

    public async Task ReplayAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ids", new BigintArrayParam(deadLetterSeqs));
        await _db.ExecuteAsync(_queries.DeadLetterReplay, parameters, ct).ConfigureAwait(false);
    }

    public async Task PurgeAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ids", new BigintArrayParam(deadLetterSeqs));
        await _db.ExecuteAsync(_queries.DeadLetterPurge, parameters, ct).ConfigureAwait(false);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        await _db.ExecuteAsync(_queries.DeadLetterPurgeAll, null, ct).ConfigureAwait(false);
    }
}
