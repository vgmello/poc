using Npgsql;
using NpgsqlTypes;

namespace Outbox.Shared;

public static class DbHelpers
{
    public static async Task<NpgsqlConnection> OpenConnectionAsync(
        string connectionString, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                return conn;
            }
            catch (NpgsqlException ex) when (IsTransientNpgsqlError(ex) && attempt < 5)
            {
                await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task InsertOutboxRowAsync(
        NpgsqlConnection conn,
        string topicName,
        string partitionKey,
        string eventType,
        string? headers,
        string payload,
        DateTime eventDateTimeUtc,
        short eventOrdinal,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO outbox (topic_name, partition_key, event_type, headers, payload, event_datetime_utc, event_ordinal)
VALUES (@topic_name, @partition_key, @event_type, @headers, @payload, @event_datetime_utc, @event_ordinal);";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@topic_name", topicName);
        cmd.Parameters.AddWithValue("@partition_key", partitionKey);
        cmd.Parameters.AddWithValue("@event_type", eventType);
        cmd.Parameters.Add(new NpgsqlParameter("@headers", NpgsqlDbType.Varchar, 4000)
        {
            Value = (object?)headers ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@event_datetime_utc", eventDateTimeUtc);
        cmd.Parameters.AddWithValue("@event_ordinal", eventOrdinal);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static bool IsTransientNpgsqlError(NpgsqlException ex)
    {
        // PostgreSQL SQLSTATE codes for transient errors
        if (ex.SqlState is "40001" or "40P01" or "08006" or "08001" or "08004"
            or "57P03" or "53300")
            return true;

        if (ex.InnerException is System.IO.IOException or System.Net.Sockets.SocketException)
            return true;

        return false;
    }
}
