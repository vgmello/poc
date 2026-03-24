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
        IOptions<PostgreSqlStoreOptions> options)
    {
        _options = options.Value;
        _db = new PostgreSqlDbHelper(connectionFactory, serviceProvider, _options);
        _queries = new PostgreSqlQueries(_options.SchemaName, _options.TablePrefix);
        DapperConfiguration.EnsureInitialized();
    }

    public async Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct)
    {
        IEnumerable<DeadLetteredMessage>? messages = null;

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            messages = await conn.QueryAsync<DeadLetteredMessage>(new CommandDefinition(_queries.DeadLetterGet,
                new { limit, offset },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return messages?.AsList() ?? [];
    }

    public async Task ReplayAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            var parameters = new DynamicParameters();
            parameters.Add("@ids", new BigintArrayParam(deadLetterSeqs));
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetterReplay, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAsync(IReadOnlyList<long> deadLetterSeqs, CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            var parameters = new DynamicParameters();
            parameters.Add("@ids", new BigintArrayParam(deadLetterSeqs));
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetterPurge, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(_queries.DeadLetterPurgeAll,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
