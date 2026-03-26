// Copyright (c) OrgName. All rights reserved.

using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Outbox.SqlServer;

internal sealed class SqlServerDbHelper
{
    private readonly Func<IServiceProvider, CancellationToken, Task<DbConnection>> _connectionFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly SqlServerStoreOptions _options;

    public SqlServerDbHelper(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        SqlServerStoreOptions options)
    {
        _connectionFactory = connectionFactory;
        _serviceProvider = serviceProvider;
        _options = options;
    }

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = (SqlConnection)await _connectionFactory(_serviceProvider, ct).ConfigureAwait(false);
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        return conn;
    }

    public async Task ExecuteWithRetryAsync(
        Func<SqlConnection, CancellationToken, Task> operation,
        CancellationToken ct)
    {
        var maxAttempts = _options.TransientRetryMaxAttempts;
        var backoffMs = _options.TransientRetryBackoffMs;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
                await operation(conn, ct).ConfigureAwait(false);

                return;
            }
            catch (SqlException ex) when (IsTransientSqlError(ex) && attempt < maxAttempts)
            {
                var delay = backoffMs * (1 << (attempt - 1));
                var jitter = Random.Shared.Next(0, delay / 4); // up to 25% jitter
                await Task.Delay(delay + jitter, ct).ConfigureAwait(false);
            }
        }
    }

    public Task<int> ExecuteAsync(string sql, object? parameters, CancellationToken ct) =>
        CallAsync<int>(sql, parameters, static conn => conn.ExecuteAsync, ct);

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken ct) =>
        CallAsync<IEnumerable<T>>(sql, parameters, static conn => conn.QueryAsync<T>, ct);

    public Task<T> ScalarAsync<T>(string sql, object? parameters, CancellationToken ct) =>
        CallAsync<T>(sql, parameters, static conn => cmd => conn.ExecuteScalarAsync<T>(cmd)!, ct);

    private async Task<TResult> CallAsync<TResult>(
        string sql,
        object? parameters,
        Func<SqlConnection, Func<CommandDefinition, Task<TResult>>> dbFunction,
        CancellationToken ct)
    {
        TResult? result = default;
        await ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            var command = new CommandDefinition(sql, parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancel);
            result = await dbFunction(conn)(command).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return result!;
    }

    public static DataTable CreateSequenceNumberTable(IReadOnlyList<long> sequenceNumbers)
    {
        var dt = new DataTable();
        dt.Columns.Add("SequenceNumber", typeof(long));
        foreach (var sn in sequenceNumbers)
            dt.Rows.Add(sn);

        return dt;
    }

    public static bool IsTransientSqlError(SqlException ex)
    {
        if (ex.Number is 1205 // deadlock victim
            or -2 // timeout
            or -1 // connection broken
            or 64 // connection lost during send
            or 233 // client unable to establish connection
            or 10053 // TCP: established connection aborted by software
            or 10054 // TCP: existing connection forcibly closed
            or 10060 // TCP: connection attempt timed out
            or 10928 or 10929 // Azure SQL resource limits
            or 40143 // connection cannot process request
            or 40197 // Azure SQL service error
            or 40501 // Azure SQL service busy
            or 40540 // Azure SQL service unavailable (read-only)
            or 40613 // Azure SQL database not available
            or 49918 or 49919 or 49920) // Azure SQL transient errors
            return true;

        // Network-level failures surfaced as inner exceptions (common during Azure SQL failover)
        if (ex.InnerException is IOException or System.Net.Sockets.SocketException)
            return true;

        return false;
    }
}
