using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Outbox.Samples.Shared;

public sealed class PostgreSqlEventProducer : EventProducerBase
{
    private readonly string _connectionString;

    public PostgreSqlEventProducer(ILogger<PostgreSqlEventProducer> logger, IConfiguration configuration)
        : base(logger)
    {
        _connectionString = configuration.GetConnectionString("OutboxDb")
            ?? throw new InvalidOperationException("ConnectionStrings:OutboxDb not set");
    }

    protected override async Task InsertOutboxRowAsync(
        string topicName, string partitionKey, string eventType,
        IReadOnlyDictionary<string, string>? headers, string payload, CancellationToken ct)
    {
        var headersJson = headers is not null ? JsonSerializer.Serialize(headers) : null;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO outbox
                (topic_name, partition_key, event_type, headers, payload, event_datetime_utc, event_ordinal)
            VALUES
                (@topicName, @partitionKey, @eventType, @headers, @payload, NOW(), 0)
            """, conn);
        cmd.Parameters.AddWithValue("@topicName", topicName);
        cmd.Parameters.AddWithValue("@partitionKey", partitionKey);
        cmd.Parameters.AddWithValue("@eventType", eventType);
        cmd.Parameters.AddWithValue("@headers", (object?)headersJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@payload", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
