// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class PoisonMessageTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public PoisonMessageTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PoisonMessage_EventuallyDeadLettered_DoesNotBlockOtherMessages()
    {
        var topic = OutboxTestHelper.UniqueTopic("poison");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Use MaxRetryCount=3 for faster test
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
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
            await InsertPoisonMessageAsync(_infra.ConnectionString, topic, poisonKey);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, topic, "healthy-key");

            await host.StartAsync();

            // Wait for processing
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var outboxCount = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
                var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);

                return outboxCount == 0 && dlqCount >= 1;
            }, TimeSpan.FromSeconds(30), message: "Poison message should be dead-lettered, healthy messages published");

            // Assert: dead letter has the poison message
            var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);
            Assert.True(dlqCount >= 1, $"Expected at least 1 dead-lettered message, got {dlqCount}");

            // Assert: healthy messages were published
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 5, TimeSpan.FromSeconds(5));
            Assert.True(consumed.Count >= 5, $"Expected at least 5 healthy messages, got {consumed.Count}");

            // Assert: outbox is empty
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));

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
        await using var conn = new Npgsql.NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO outbox (topic_name, partition_key, event_type, payload, event_datetime_utc, event_ordinal)
            VALUES (@topic, @key, 'PoisonEvent', @payload, clock_timestamp(), 0)", conn);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@key", partitionKey);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Bytea)
        {
            Value = System.Text.Encoding.UTF8.GetBytes("{\"poison\":true}")
        });
        await cmd.ExecuteNonQueryAsync();
    }
}
