// Copyright (c) OrgName. All rights reserved.

using Microsoft.Data.SqlClient;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.SqlServer;

[Collection(InfrastructureCollection.Name)]
public class SqlServerPoisonMessageTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public SqlServerPoisonMessageTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PoisonMessage_EventuallyDeadLettered_DoesNotBlockOtherMessages()
    {
        var topic = OutboxTestHelper.UniqueTopic("ss-poison");
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);

        // Use MaxRetryCount=3 for faster test
        var (host, transport) = SqlServerTestHelper.BuildPublisherHost(
            _infra.SqlServerConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.MaxRetryCount = 4;
                o.CircuitBreakerFailureThreshold = 3;
            });

        // Make transport fail for a specific partition key (simulating oversized message)
        var poisonKey = "poison-key";
        transport.SetIntermittentPredicate(msg => msg.PartitionKey == poisonKey);

        try
        {
            // Insert poison message + normal messages
            await InsertPoisonMessageAsync(_infra.SqlServerConnectionString, topic, poisonKey);
            await SqlServerTestHelper.InsertMessagesAsync(_infra.SqlServerConnectionString, 5, topic, "healthy-key");

            await host.StartAsync();

            // Wait for processing
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var outboxCount = await SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString);
                var dlqCount = await SqlServerTestHelper.GetDeadLetterCountAsync(_infra.SqlServerConnectionString);

                return outboxCount == 0 && dlqCount >= 1;
            }, TimeSpan.FromSeconds(30), message: "Poison message should be dead-lettered, healthy messages published");

            // Assert: dead letter has the poison message
            var dlqCount = await SqlServerTestHelper.GetDeadLetterCountAsync(_infra.SqlServerConnectionString);
            Assert.True(dlqCount >= 1, $"Expected at least 1 dead-lettered message, got {dlqCount}");

            // Assert: healthy messages were published
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 5, TimeSpan.FromSeconds(5));
            Assert.True(consumed.Count >= 5, $"Expected at least 5 healthy messages, got {consumed.Count}");

            // Assert: outbox is empty
            Assert.Equal(0, await SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString));

            _output.WriteLine($"Poison message dead-lettered. DLQ count: {dlqCount}. Consumed: {consumed.Count}");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task InsertPoisonMessageAsync(string connectionString, string topic, string partitionKey)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        const string sql = @"
            INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Payload, EventDateTimeUtc, EventOrdinal)
            VALUES (@topic, @key, 'PoisonEvent', @payload, SYSUTCDATETIME(), 0)";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@key", partitionKey);
        cmd.Parameters.Add(new SqlParameter("@payload", System.Data.SqlDbType.VarBinary)
        {
            Value = System.Text.Encoding.UTF8.GetBytes("{\"poison\":true}")
        });
        await cmd.ExecuteNonQueryAsync();
    }
}
