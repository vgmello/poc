// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub.SqlServer;

[Collection(InfrastructureCollection.Name)]
public class SqlServerBrokerDownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public SqlServerBrokerDownTests(InfrastructureFixture infra, ITestOutputHelper output)
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
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);

        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.SqlServerConnectionString, _infra.EventHubConnectionString,
            useSqlServer: true);

        try
        {
            await host.StartAsync();

            // Phase 1: Publish some messages successfully
            await SqlServerTestHelper.InsertMessagesAsync(_infra.SqlServerConnectionString, 10, eventHub, partitionKey);
            await OutboxTestHelper.WaitUntilAsync(
                () => SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(15), message: "Initial messages should drain");

            var initialMessages = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 10, TimeSpan.FromSeconds(5), partitionKey);
            Assert.Equal(10, initialMessages.Count);

            // Phase 2: Block broker, insert more messages
            transport.SetFailing(true);
            _output.WriteLine("Broker set to failing");

            await SqlServerTestHelper.InsertMessagesAsync(_infra.SqlServerConnectionString, 50, eventHub, partitionKey);
            await Task.Delay(TimeSpan.FromSeconds(8)); // Allow circuit breaker to open

            // Assert: messages still in outbox
            var pending = await SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString);
            Assert.True(pending > 0, $"Messages should accumulate in outbox, got {pending}");
            _output.WriteLine($"Pending messages during outage: {pending}");

            // Phase 3: Restore broker
            transport.SetFailing(false);
            _output.WriteLine("Broker restored");

            // Wait for circuit to half-open and messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain after broker recovery");

            // Assert: all messages consumed (10 initial + 50 during outage, possibly with duplicates)
            var allMessages = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 60, TimeSpan.FromSeconds(10), partitionKey);
            Assert.True(allMessages.Count >= 50,
                $"Expected at least 50 new messages, got {allMessages.Count} total (includes initial 10 + possible duplicates)");

            // Assert: outbox is empty
            Assert.Equal(0, await SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString));
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
