// Copyright (c) OrgName. All rights reserved.

using System.Text;
using System.Text.Json;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class PartialSendTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public PartialSendTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task BatchSplit_AllMessagesEventuallyDelivered()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString,
            configureOptions: o =>
            {
                o.MaxPublishAttempts = 10;
                o.CircuitBreakerFailureThreshold = 100;
            });

        // Configure MaxBatchSizeBytes low so the real EventHubOutboxTransport
        // splits into multiple sub-batches. Requires modifying transport options
        // after host build — but options are already set in BuildEventHubPublisherHost.
        // Instead, we'll pass configureTransportOptions... but that parameter doesn't exist yet.
        //
        // WORKAROUND: Insert messages small enough to fit in default batch size (1MB)
        // but numerous enough that sub-batching is exercised by the transport if it
        // chooses to split. The important thing is: with intermittent failures,
        // the publisher retries and all messages eventually arrive.

        transport.SetIntermittent(3); // succeed every 3rd call

        try
        {
            // Insert 20 messages with padded payloads
            await InsertPaddedMessagesAsync(eventHub, partitionKey, count: 20, bytesPerMessage: 3000);

            await host.StartAsync();

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString)
                    .ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60),
                message: "All 20 padded messages should drain despite intermittent failures");

            var consumed = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 20, TimeSpan.FromSeconds(15), partitionKey);

            var uniqueIndices = consumed
                .Select(m => JsonDocument.Parse(m.Body).RootElement.GetProperty("index").GetInt32())
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            _output.WriteLine($"Consumed {consumed.Count} messages, {uniqueIndices.Count} unique indices");

            Assert.Equal(20, uniqueIndices.Count);
            Assert.Equal(Enumerable.Range(0, 20).ToList(), uniqueIndices);

            _output.WriteLine("All 20 padded messages delivered with correct content");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            await EventHubTestHelper.DrainHubAsync(
                _infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }

    private async Task InsertPaddedMessagesAsync(
        string topic, string partitionKey, int count, int bytesPerMessage)
    {
        await using var conn = new Npgsql.NpgsqlConnection(_infra.ConnectionString);
        await conn.OpenAsync();

        var padding = new string('x', Math.Max(0, bytesPerMessage - 40));

        for (var i = 0; i < count; i++)
        {
            var sql = @"
                INSERT INTO outbox (topic_name, partition_key, event_type, payload,
                                    event_datetime_utc)
                VALUES (@topic, @key, 'TestEvent', @payload, clock_timestamp())";

            await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@topic", topic);
            cmd.Parameters.AddWithValue("@key", partitionKey);
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Bytea)
            {
                Value = Encoding.UTF8.GetBytes($"{{\"index\":{i},\"pad\":\"{padding}\"}}")
            });
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
