using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Outbox.Samples.Shared;

public sealed class SqlServerEventProducer : EventProducerBase
{
    private readonly string _connectionString;

    public SqlServerEventProducer(ILogger<SqlServerEventProducer> logger, IConfiguration configuration)
        : base(logger)
    {
        _connectionString = configuration.GetConnectionString("OutboxDb")
            ?? throw new InvalidOperationException("ConnectionStrings:OutboxDb not set");
    }

    protected override async Task InsertOutboxRowAsync(
        string topicName, string partitionKey, string eventType,
        IReadOnlyDictionary<string, string>? headers,
        string payload, CancellationToken ct)
    {
        var headersJson = headers is null ? null : JsonSerializer.Serialize(headers);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            INSERT INTO dbo.Outbox
                (TopicName, PartitionKey, EventType, Headers, Payload, EventDateTimeUtc, EventOrdinal)
            VALUES
                (@topicName, @partitionKey, @eventType, @headers, @payload, SYSUTCDATETIME(), 0)
            """, conn);
        cmd.Parameters.AddWithValue("@topicName", topicName);
        cmd.Parameters.AddWithValue("@partitionKey", partitionKey);
        cmd.Parameters.AddWithValue("@eventType", eventType);
        cmd.Parameters.AddWithValue("@headers", (object?)headersJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@payload", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
