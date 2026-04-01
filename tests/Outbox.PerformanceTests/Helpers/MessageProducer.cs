using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Outbox.PerformanceTests.Helpers;

public static class MessageProducer
{
    private const int TopicCount = 3;
    private const int PartitionKeyCount = 10;
    private static readonly byte[] FixedPayload = Encoding.UTF8.GetBytes(
        """{"perf":true,"data":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");

    // ---------------------------------------------------------------
    // Bulk seeding — used by BulkThroughputTests
    // ---------------------------------------------------------------

    public static async Task BulkSeedAsync(StoreType store, string connectionString,
        int totalMessages, int batchSize = 10_000)
    {
        // SQL Server limits INSERT VALUES to 1000 rows per statement
        var effectiveBatchSize = store == StoreType.SqlServer
            ? Math.Min(batchSize, 1000)
            : batchSize;

        for (var offset = 0; offset < totalMessages; offset += effectiveBatchSize)
        {
            var count = Math.Min(effectiveBatchSize, totalMessages - offset);

            if (store == StoreType.PostgreSql)
                await InsertBatchPostgreSql(connectionString, offset, count);
            else
                await InsertBatchSqlServer(connectionString, offset, count);
        }
    }

    // ---------------------------------------------------------------
    // Sustained-rate insertion — used by SustainedLoadTests
    // ---------------------------------------------------------------

    public static async Task ProduceAtRateAsync(StoreType store, string connectionString,
        int messagesPerSecond, CancellationToken ct)
    {
        const int insertsPerSecond = 10; // 10 micro-batches per second
        var batchSize = messagesPerSecond / insertsPerSecond;
        var interval = TimeSpan.FromMilliseconds(1000.0 / insertsPerSecond);
        var sw = Stopwatch.StartNew();
        var batchIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            var expectedTime = interval * batchIndex;
            var elapsed = sw.Elapsed;
            var delay = expectedTime - elapsed;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            var offset = batchIndex * batchSize;

            try
            {
                if (store == StoreType.PostgreSql)
                    await InsertBatchPostgreSql(connectionString, offset, count: batchSize);
                else
                    await InsertBatchSqlServer(connectionString, offset, count: batchSize);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            batchIndex++;
        }
    }

    // ---------------------------------------------------------------
    // PostgreSQL multi-row insert
    // ---------------------------------------------------------------

    private static async Task InsertBatchPostgreSql(string connectionString, int offset, int count)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var sb = new StringBuilder();
        sb.Append("""
            INSERT INTO outbox (topic_name, partition_key, event_type, payload, event_datetime_utc, event_ordinal)
            VALUES
            """);

        for (var i = 0; i < count; i++)
        {
            var globalIndex = offset + i;
            var topic = $"perf-topic-{globalIndex % TopicCount}";
            var key = $"pk-{globalIndex % PartitionKeyCount}";

            if (i > 0) sb.Append(',');
            sb.Append($"('{topic}','{key}','PerfEvent',@p,clock_timestamp(),{i})");
        }

        await using var cmd = new NpgsqlCommand(sb.ToString(), conn);
        cmd.Parameters.Add(new NpgsqlParameter("p", FixedPayload));
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------
    // SQL Server multi-row insert
    // ---------------------------------------------------------------

    private static async Task InsertBatchSqlServer(string connectionString, int offset, int count)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sb = new StringBuilder();
        sb.Append("""
            INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Payload, EventDateTimeUtc, EventOrdinal)
            VALUES
            """);

        for (var i = 0; i < count; i++)
        {
            var globalIndex = offset + i;
            var topic = $"perf-topic-{globalIndex % TopicCount}";
            var key = $"pk-{globalIndex % PartitionKeyCount}";

            if (i > 0) sb.Append(',');
            sb.Append($"('{topic}','{key}','PerfEvent',@p,SYSUTCDATETIME(),{i})");
        }

        await using var cmd = new SqlCommand(sb.ToString(), conn);
        cmd.Parameters.Add(new SqlParameter("@p", System.Data.SqlDbType.VarBinary) { Value = FixedPayload });
        await cmd.ExecuteNonQueryAsync();
    }
}
