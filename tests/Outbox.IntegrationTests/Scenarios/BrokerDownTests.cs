// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

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
        var topic = OutboxTestHelper.UniqueTopic("broker-down");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await host.StartAsync();

            // Phase 1: Publish some messages successfully
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(15), message: "Initial messages should drain");

            var initialMessages = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 10, TimeSpan.FromSeconds(5));
            Assert.Equal(10, initialMessages.Count);

            // Phase 2: Block broker, insert more messages
            transport.SetFailing(true);
            _output.WriteLine("Broker set to failing");

            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic, "key-1");
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
            var allMessages = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 60, TimeSpan.FromSeconds(10));
            Assert.True(allMessages.Count >= 50,
                $"Expected at least 50 new messages, got {allMessages.Count} total (includes initial 10 + possible duplicates)");

            // Assert: outbox is empty
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task NetworkPartition_DbReachable_BrokerNot_CircuitOpensAndRecovers()
    {
        // Scenario 14: DB works, broker doesn't
        var topic = OutboxTestHelper.UniqueTopic("net-partition");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await host.StartAsync();

            // Block broker only (DB stays up)
            transport.SetFailing(true);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, topic, "key-1");
            await Task.Delay(TimeSpan.FromSeconds(8));

            // Assert: messages accumulate, heartbeat still works (DB is fine)
            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            Assert.True(pending > 0, "Messages should accumulate");

            // Producers should still be registered (heartbeat works)
            var producers = await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString);
            Assert.Single(producers);

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
            host.Dispose();
        }
    }
}
