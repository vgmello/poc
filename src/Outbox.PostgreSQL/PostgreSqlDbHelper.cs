using System.Data.Common;
using Npgsql;

namespace Outbox.PostgreSQL;

internal sealed class PostgreSqlDbHelper
{
    private readonly Func<IServiceProvider, CancellationToken, Task<DbConnection>> _connectionFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly PostgreSqlStoreOptions _options;

    public PostgreSqlDbHelper(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        PostgreSqlStoreOptions options)
    {
        _connectionFactory = connectionFactory;
        _serviceProvider = serviceProvider;
        _options = options;
    }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = await _connectionFactory(_serviceProvider, ct).ConfigureAwait(false);
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public NpgsqlCommand CreateCommand(string sql, DbConnection conn)
    {
        var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        return cmd;
    }

    public async Task ExecuteWithRetryAsync(
        Func<DbConnection, CancellationToken, Task> action,
        CancellationToken ct)
    {
        int maxAttempts = _options.TransientRetryMaxAttempts;
        int backoffMs = _options.TransientRetryBackoffMs;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
                await action(conn, ct).ConfigureAwait(false);
                return;
            }
            catch (NpgsqlException ex) when (IsTransientNpgsqlError(ex) && attempt < maxAttempts)
            {
                await Task.Delay(backoffMs * (1 << (attempt - 1)), ct).ConfigureAwait(false);
            }
        }
    }

    public static bool IsTransientNpgsqlError(NpgsqlException ex)
    {
        if (ex.SqlState is not null)
        {
            if (ex.SqlState is "40001" or "40P01" or "57P01")
                return true;
            if (ex.SqlState.StartsWith("08", StringComparison.Ordinal))
                return true;
            if (ex.SqlState.StartsWith("53", StringComparison.Ordinal))
                return true;
        }

        if (ex.InnerException is System.IO.IOException or System.Net.Sockets.SocketException)
            return true;

        return false;
    }
}
