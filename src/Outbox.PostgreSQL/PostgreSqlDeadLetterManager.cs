using System.Data.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.PostgreSQL;

public sealed class PostgreSqlDeadLetterManager : IDeadLetterManager
{
    private readonly Func<IServiceProvider, CancellationToken, Task<DbConnection>> _connectionFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly PostgreSqlStoreOptions _options;

    public PostgreSqlDeadLetterManager(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<PostgreSqlStoreOptions> options)
    {
        _connectionFactory = connectionFactory;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct)
    {
        const string sql = @"
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       event_datetime_utc, event_ordinal, retry_count, created_at_utc,
       dead_lettered_at_utc, last_error
FROM   outbox_dead_letter
ORDER  BY dead_letter_seq
LIMIT  @limit OFFSET @offset;";

        var messages = new List<DeadLetteredMessage>();

        await ExecuteWithRetryAsync(async (conn, token) =>
        {
            messages.Clear();
            await using var cmd = CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                messages.Add(new DeadLetteredMessage(
                    SequenceNumber: reader.GetInt64(0),
                    TopicName: reader.GetString(1),
                    PartitionKey: reader.GetString(2),
                    EventType: reader.GetString(3),
                    Headers: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Payload: reader.GetString(5),
                    EventDateTimeUtc: reader.GetFieldValue<DateTimeOffset>(6),
                    EventOrdinal: reader.GetInt16(7),
                    RetryCount: reader.GetInt32(8),
                    CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(9),
                    DeadLetteredAtUtc: reader.GetFieldValue<DateTimeOffset>(10),
                    LastError: reader.IsDBNull(11) ? null : reader.GetString(11)));
            }
        }, ct).ConfigureAwait(false);

        return messages;
    }

    public async Task ReplayAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
WITH replayed AS (
    DELETE FROM outbox_dead_letter
    WHERE  sequence_number = ANY(@ids)
    RETURNING sequence_number, topic_name, partition_key, event_type, headers, payload,
              created_at_utc, event_datetime_utc, event_ordinal
)
INSERT INTO outbox
    (topic_name, partition_key, event_type, headers, payload,
     created_at_utc, event_datetime_utc, event_ordinal,
     leased_until_utc, lease_owner, retry_count)
SELECT topic_name, partition_key, event_type, headers, payload,
       created_at_utc, event_datetime_utc, event_ordinal,
       NULL, NULL, 0
FROM replayed;";

        await ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = CreateCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequenceNumbers.ToArray()
            });
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
DELETE FROM outbox_dead_letter
WHERE  sequence_number = ANY(@ids);";

        await ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = CreateCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequenceNumbers.ToArray()
            });
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        const string sql = "DELETE FROM outbox_dead_letter;";

        await ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = CreateCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = await _connectionFactory(_serviceProvider, ct).ConfigureAwait(false);
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private NpgsqlCommand CreateCommand(string sql, DbConnection conn)
    {
        var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        return cmd;
    }

    private async Task ExecuteWithRetryAsync(
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
                await Task.Delay(backoffMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientNpgsqlError(NpgsqlException ex)
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
