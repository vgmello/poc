// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class BrokerDownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public BrokerDownTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task BrokerDown_MessagesAccumulate_ThenDrainOnRecovery()
    {
        // Arrange
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString);

        try
        {
            await host.StartAsync();

            // Wait for publisher to register and claim partitions (rebalance loop delays 3s before first action)
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
                return owners.Values.Any(v => v != null);
            }, TimeSpan.FromSeconds(10), message: "Publisher should claim partitions");

            // Phase 1: Publish some messages successfully
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, eventHub, partitionKey);
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(15), message: "Initial messages should drain");

            var initialMessages = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 10, TimeSpan.FromSeconds(5), partitionKey);
            Assert.Equal(10, initialMessages.Count);

            // Phase 2: Block broker, insert more messages
            transport.SetFailing(true);
            _output.WriteLine("Broker set to failing");

            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, eventHub, partitionKey);
            await Task.Delay(TimeSpan.FromSeconds(8)); // Allow circuit breaker to open

            // Assert: messages still in outbox
            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            Assert.True(pending > 0, $"Messages should accumulate in outbox, got {pending}");
            _output.WriteLine($"Pending messages during outage: {pending}");

            // Phase 3: Restore broker
            transport.SetFailing(false);
            _output.WriteLine("Broker restored");

            // Wait for circuit to half-open and messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain after broker recovery");

            // Assert: all messages consumed (10 initial + 50 during outage, possibly with duplicates)
            var allMessages = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 60, TimeSpan.FromSeconds(10), partitionKey);
            Assert.True(allMessages.Count >= 50,
                $"Expected at least 50 new messages, got {allMessages.Count} total (includes initial 10 + possible duplicates)");

            // Assert: outbox is empty
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }

    [Fact]
    public async Task NetworkPartition_DbReachable_BrokerNot_CircuitOpensAndRecovers()
    {
        // Scenario 14: DB works, broker doesn't
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString);

        try
        {
            await host.StartAsync();

            // Wait for publisher to register and claim partitions (rebalance loop delays 3s before first action)
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
                return owners.Values.Any(v => v != null);
            }, TimeSpan.FromSeconds(10), message: "Publisher should claim partitions");

            // Block broker only (DB stays up)
            transport.SetFailing(true);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, eventHub, partitionKey);
            await Task.Delay(TimeSpan.FromSeconds(8));

            // Assert: messages accumulate, heartbeat still works (DB is fine)
            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            Assert.True(pending > 0, "Messages should accumulate");

            // Publishers should still be registered (heartbeat works)
            var publishers = await OutboxTestHelper.GetPublisherIdsAsync(_infra.ConnectionString);
            Assert.Single(publishers);

            // Restore broker
            transport.SetFailing(false);

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain after broker recovery");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }
}
