using System.Data;
using System.Data.Common;
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
        int maxAttempts = _options.TransientRetryMaxAttempts;
        int backoffMs = _options.TransientRetryBackoffMs;

        for (int attempt = 1; ; attempt++)
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

    public static void AddSequenceNumberTvp(
        SqlCommand cmd, string paramName, IReadOnlyList<long> sequenceNumbers, string schema)
    {
        var dt = new DataTable();
        dt.Columns.Add("SequenceNumber", typeof(long));
        foreach (var sn in sequenceNumbers)
            dt.Rows.Add(sn);

        var param = cmd.Parameters.AddWithValue(paramName, dt);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName = $"{schema}.SequenceNumberList";
    }

    public static bool IsTransientSqlError(SqlException ex)
        => ex.Number is 1205    // deadlock victim
            or -2               // timeout
            or 40613            // Azure SQL database not available
            or 40197            // Azure SQL service error
            or 40501            // Azure SQL service busy
            or 49918 or 49919 or 49920; // Azure SQL transient errors
}
