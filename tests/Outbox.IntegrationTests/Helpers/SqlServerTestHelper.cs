// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using System.Text;
using Confluent.Kafka;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;
using Outbox.SqlServer;

namespace Outbox.IntegrationTests.Helpers;

/// <summary>
///     SQL Server equivalent of <see cref="ToggleableConnectionFactory" />.
/// </summary>
public sealed class SqlServerToggleableConnectionFactory
{
    private readonly string _connectionString;
    private volatile bool _failing;

    public SqlServerToggleableConnectionFactory(string connectionString) =>
        _connectionString = connectionString;

    public void SetFailing(bool failing) => _failing = failing;

    public Task<DbConnection> CreateConnectionAsync(IServiceProvider sp, CancellationToken ct)
    {
        if (_failing)
            throw new InvalidOperationException("Simulated SQL Server connection failure");

        return Task.FromResult<DbConnection>(new SqlConnection(_connectionString));
    }
}

public static class SqlServerTestHelper
{
    // ------------------------------------------------------------------
    // Host builder
    // ------------------------------------------------------------------

    public static (IHost Host, FaultyTransportWrapper Transport) BuildPublisherHost(
        string connectionString,
        string bootstrapServers,
        Action<OutboxPublisherOptions>? configureOptions = null,
        SqlServerToggleableConnectionFactory? connectionFactory = null)
    {
        connectionFactory ??= new SqlServerToggleableConnectionFactory(connectionString);

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
                    outbox.UseSqlServer(options =>
                    {
                        options.ConnectionFactory = connectionFactory.CreateConnectionAsync;
                    });
                    outbox.ConfigurePublisher(o =>
                    {
                        OutboxTestHelper.FastTestOptions(o);
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
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        for (var i = 0; i < count; i++)
        {
            var key = partitionKey ?? $"key-{i % 10}";
            const string sql = @"
                INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Payload, EventDateTimeUtc)
                VALUES (@topic, @key, 'TestEvent', @payload, SYSUTCDATETIME())";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@topic", topic);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.Add(new SqlParameter("@Payload", System.Data.SqlDbType.VarBinary)
            {
                Value = Encoding.UTF8.GetBytes($"{{\"index\":{startOrdinal + i}}}")
            });
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ------------------------------------------------------------------
    // DB query helpers
    // ------------------------------------------------------------------

    public static async Task<long> GetOutboxCountAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Outbox", conn);

        return (long)(int)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<long> GetDeadLetterCountAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.OutboxDeadLetter", conn);

        return (long)(int)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<Dictionary<int, string?>> GetPartitionOwnersAsync(string connectionString)
    {
        var map = new Dictionary<int, string?>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT PartitionId, OwnerPublisherId FROM dbo.OutboxPartitions ORDER BY PartitionId", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            map[reader.GetInt32(0)] = await reader.IsDBNullAsync(1) ? null : reader.GetString(1);
        }

        return map;
    }

    public static async Task<List<string>> GetPublisherIdsAsync(string connectionString)
    {
        var ids = new List<string>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT PublisherId FROM dbo.OutboxPublishers", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    // ------------------------------------------------------------------
    // Cleanup — called at the start of each test
    // ------------------------------------------------------------------

    public static async Task CleanupAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        // Use DELETE instead of TRUNCATE because TRUNCATE may fail due to FK constraints
        await using var cmd = new SqlCommand(@"
            DELETE FROM dbo.Outbox;
            DELETE FROM dbo.OutboxDeadLetter;
            DELETE FROM dbo.OutboxPublishers;
            UPDATE dbo.OutboxPartitions SET OwnerPublisherId = NULL, OwnedSinceUtc = NULL, GraceExpiresUtc = NULL;",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
