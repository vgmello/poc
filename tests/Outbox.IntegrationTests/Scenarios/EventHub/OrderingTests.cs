// Copyright (c) OrgName. All rights reserved.

using System.Text.Json;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class OrderingTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public OrderingTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PerPartitionKeyOrdering_PreservedThroughFailures()
    {
        var eventHub = EventHubTestHelper.CheckoutNamedHub("test-hub-ordering-8p");
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString,
            o =>
            {
                o.CircuitBreakerFailureThreshold = 100; // Don't open circuit
                o.MaxPublishAttempts = 101; // Higher than circuit threshold
            });

        // Intermittent failures: succeed every 3rd call
        transport.SetIntermittent(3);

        try
        {
            // Insert 50 ordered messages with sequential ordinals
            await OutboxTestHelper.InsertMessagesAsync(
                _infra.ConnectionString, 50, eventHub, partitionKey, startOrdinal: 0);

            await host.StartAsync();

            // Wait for all to publish
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All ordered messages should drain");

            // Consume and verify order
            var consumed = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 50, TimeSpan.FromSeconds(10), partitionKey);

            // Extract index from payload {"index":N}
            var indices = consumed
                .Select(m =>
                {
                    var doc = JsonDocument.Parse(m.Body);

                    return doc.RootElement.GetProperty("index").GetInt32();
                })
                .ToList();

            _output.WriteLine($"Consumed {consumed.Count} messages. Indices (first 20): {string.Join(", ", indices.Take(20))}");

            // Deduplicate (at-least-once may produce duplicates)
            var deduped = new List<int>();
            var seen = new HashSet<int>();

            foreach (var idx in indices)
            {
                if (seen.Add(idx))
                    deduped.Add(idx);
            }

            // Assert: all 50 messages present
            Assert.Equal(50, deduped.Count);

            // Assert: ordering preserved (each element > previous)
            for (var i = 1; i < deduped.Count; i++)
            {
                Assert.True(deduped[i] > deduped[i - 1],
                    $"Ordering violation: index {deduped[i]} came after {deduped[i - 1]} at position {i}");
            }

            _output.WriteLine("Ordering verified: all 50 messages in order, no gaps");
        }
        finally
        {
            transport.Reset();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            await host.StopAsync();
            host.Dispose();
        }
    }
}
