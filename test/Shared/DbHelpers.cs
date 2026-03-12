using System.Data;
using Microsoft.Data.SqlClient;

namespace Outbox.Shared;

public static class DbHelpers
{
    public static async Task<SqlConnection> OpenConnectionAsync(
        string connectionString, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                return conn;
            }
            catch (SqlException ex) when (IsTransientSqlError(ex) && attempt < 5)
            {
                await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task InsertOutboxRowAsync(
        SqlConnection conn,
        string topicName,
        string partitionKey,
        string eventType,
        string? headers,
        string payload,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Headers, Payload)
VALUES (@TopicName, @PartitionKey, @EventType, @Headers, @Payload);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TopicName", topicName);
        cmd.Parameters.AddWithValue("@PartitionKey", partitionKey);
        cmd.Parameters.AddWithValue("@EventType", eventType);
        cmd.Parameters.Add("@Headers", SqlDbType.NVarChar, 4000).Value =
            (object?)headers ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@Payload", payload);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static DataTable BuildSequenceNumberTvp(IEnumerable<long> sequenceNumbers)
    {
        var dt = new DataTable();
        dt.Columns.Add("SequenceNumber", typeof(long));
        foreach (var sn in sequenceNumbers)
            dt.Rows.Add(sn);
        return dt;
    }

    public static bool IsTransientSqlError(SqlException ex)
        => ex.Number is 1205 or -2 or 40613 or 40197 or 40501
            or 49918 or 49919 or 49920;
}
