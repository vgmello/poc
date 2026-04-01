// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;
using Outbox.PostgreSQL;

namespace Outbox.IntegrationTests.Helpers;

public sealed record ConsumedMessage(string Key, byte[] Value, long Offset);

public static class OutboxTestHelper
{
    /// <summary>Fast publisher options for integration tests.</summary>
    public static readonly Action<OutboxPublisherOptions> FastTestOptions = o =>
    {
        o.BatchSize = 10;
        o.MaxRetryCount = 5;
        o.MinPollIntervalMs = 50;
        o.MaxPollIntervalMs = 500;
        o.HeartbeatIntervalMs = 2_000;
        o.HeartbeatTimeoutSeconds = 7;
        o.PartitionGracePeriodSeconds = 15;
        o.RebalanceIntervalMs = 3_000;
        o.OrphanSweepIntervalMs = 3_000;
        o.DeadLetterSweepIntervalMs = 3_000;
        o.CircuitBreakerFailureThreshold = 3;
        o.CircuitBreakerOpenDurationSeconds = 5;
    };

    // ------------------------------------------------------------------
    // Host builder
    // ------------------------------------------------------------------

    public static (IHost Host, FaultyTransportWrapper Transport) BuildPublisherHost(
        string connectionString,
        string bootstrapServers,
        Action<OutboxPublisherOptions>? configureOptions = null,
        ToggleableConnectionFactory? connectionFactory = null)
    {
        connectionFactory ??= new ToggleableConnectionFactory(connectionString);

        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Empty section so Configure<T> binds cleanly
                    ["Outbox:Publisher:BatchSize"] = "10"
                });
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddOutbox(ctx.Configuration, outbox =>
                {
                    outbox.UsePostgreSql(options =>
                    {
                        options.ConnectionFactory = connectionFactory.CreateConnectionAsync;
                    });
                    outbox.ConfigurePublisher(o =>
                    {
                        FastTestOptions(o);
                        configureOptions?.Invoke(o);
                    });
                });

                // Register Kafka producer + faulty transport wrapper (replaces TryAdd from UseKafka)
                services.AddSingleton<IProducer<string, byte[]>>(_ =>
                    new ProducerBuilder<string, byte[]>(new ProducerConfig
                    {
                        BootstrapServers = bootstrapServers,
                        Acks = Acks.All,
                        EnableIdempotence = true,
                        LingerMs = 5
                    }).Build());

                services.AddSingleton<FaultyTransportWrapper>();
                services.AddSingleton<IOutboxTransport>(sp =>
                    sp.GetRequiredService<FaultyTransportWrapper>());
            })
            .Build();

        var transport = host.Services.GetRequiredService<FaultyTransportWrapper>();

        return (host, transport);
    }

    // ------------------------------------------------------------------
    // Message insertion (direct SQL — simulates the application writing to outbox)
    // ------------------------------------------------------------------

    public static async Task InsertMessagesAsync(
        string connectionString, int count, string topic,
        string? partitionKey = null, int startOrdinal = 0)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        for (var i = 0; i < count; i++)
        {
            var key = partitionKey ?? $"key-{i % 10}";
            var sql = @"
                INSERT INTO outbox (topic_name, partition_key, event_type, payload,
                                    event_datetime_utc, event_ordinal)
                VALUES (@topic, @key, 'TestEvent', @payload, clock_timestamp(), @ordinal)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@topic", topic);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Bytea)
            {
                Value = Encoding.UTF8.GetBytes($"{{\"index\":{i}}}")
            });
            cmd.Parameters.AddWithValue("@ordinal", (short)(startOrdinal + i));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ------------------------------------------------------------------
    // Kafka consumer — collects messages from a topic
    // ------------------------------------------------------------------

    public static async Task<List<ConsumedMessage>> ConsumeMessagesAsync(
        string bootstrapServers, string topic, int expectedCount, TimeSpan timeout)
    {
        var messages = new List<ConsumedMessage>();
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + timeout;

        while (messages.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(200));

                if (result?.Message != null)
                {
                    messages.Add(new ConsumedMessage(
                        result.Message.Key, result.Message.Value, result.Offset.Value));
                }
            }
            catch (ConsumeException)
            {
                /* topic may not exist yet */
            }
        }

        consumer.Close();

        return messages;
    }

    // ------------------------------------------------------------------
    // DB query helpers
    // ------------------------------------------------------------------

    public static async Task<long> GetOutboxCountAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM outbox", conn);

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<long> GetDeadLetterCountAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM outbox_dead_letter", conn);

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<List<(long Seq, int RetryCount)>> GetRetryCountsAsync(string connectionString)
    {
        var results = new List<(long, int)>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT sequence_number, retry_count FROM outbox ORDER BY sequence_number", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        return results;
    }

    public static async Task<List<string>> GetPublisherIdsAsync(string connectionString)
    {
        var ids = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT publisher_id FROM outbox_publishers", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public static async Task<Dictionary<int, string?>> GetPartitionOwnersAsync(string connectionString)
    {
        var map = new Dictionary<int, string?>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT partition_id, owner_publisher_id FROM outbox_partitions ORDER BY partition_id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            map[reader.GetInt32(0)] = await reader.IsDBNullAsync(1) ? null : reader.GetString(1);
        }

        return map;
    }

    // ------------------------------------------------------------------
    // Cleanup — called at the start of each test
    // ------------------------------------------------------------------

    public static async Task CleanupAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            TRUNCATE outbox, outbox_dead_letter, outbox_publishers;
            UPDATE outbox_partitions SET owner_publisher_id = NULL, owned_since_utc = NULL, grace_expires_utc = NULL;",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------
    // Wait helpers
    // ------------------------------------------------------------------

    public static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout,
        TimeSpan? pollInterval = null, string? message = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;

            await Task.Delay(interval);
        }

        throw new TimeoutException(message ?? $"Condition not met within {timeout}");
    }

    public static string UniqueTopic(string prefix = "test") =>
        $"{prefix}-{Guid.NewGuid():N}";
}
