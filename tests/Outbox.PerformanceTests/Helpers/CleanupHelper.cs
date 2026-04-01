using Microsoft.Data.SqlClient;
using Npgsql;

namespace Outbox.PerformanceTests.Helpers;

public static class CleanupHelper
{
    public static async Task CleanupAsync(StoreType store, string connectionString)
    {
        if (store == StoreType.PostgreSql)
            await CleanupPostgreSqlAsync(connectionString);
        else
            await CleanupSqlServerAsync(connectionString);
    }

    public static async Task<long> GetPendingCountAsync(StoreType store, string connectionString)
    {
        if (store == StoreType.PostgreSql)
            return await QueryScalarPostgreSql(connectionString, "SELECT COUNT(*) FROM outbox");
        else
            return await QueryScalarSqlServer(connectionString, "SELECT COUNT(*) FROM dbo.Outbox");
    }

    private static async Task CleanupPostgreSqlAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            TRUNCATE outbox, outbox_dead_letter;
            DELETE FROM outbox_publishers;
            UPDATE outbox_partitions SET owner_publisher_id = NULL, owned_since_utc = NULL, grace_expires_utc = NULL;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CleanupSqlServerAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            DELETE FROM dbo.Outbox;
            DELETE FROM dbo.OutboxDeadLetter;
            DELETE FROM dbo.OutboxPublishers;
            UPDATE dbo.OutboxPartitions SET OwnerPublisherId = NULL, OwnedSinceUtc = NULL, GraceExpiresUtc = NULL;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> QueryScalarPostgreSql(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> QueryScalarSqlServer(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (long)(int)(await cmd.ExecuteScalarAsync())!;
    }
}
