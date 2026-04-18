// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub.SqlServer;

[Collection(InfrastructureCollection.Name)]
public class SqlServerGracefulShutdownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public SqlServerGracefulShutdownTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task GracefulShutdown_UnregistersPublisher_NewPublisherPicksUpImmediately()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);

        var (hostA, _) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.SqlServerConnectionString, _infra.EventHubConnectionString,
            o =>
            {
                o.PartitionGracePeriodSeconds = 180;
            },
            useSqlServer: true);

        await hostA.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3)); // Let A register and claim partitions

        // Insert messages
        await SqlServerTestHelper.InsertMessagesAsync(_infra.SqlServerConnectionString, 30, eventHub, partitionKey);
        await Task.Delay(TimeSpan.FromSeconds(2)); // Let A begin processing

        // Graceful shutdown
        await hostA.StopAsync();
        hostA.Dispose();
        _output.WriteLine("Host A stopped gracefully");

        // Assert: publisher unregistered
        var publishers = await SqlServerTestHelper.GetPublisherIdsAsync(_infra.SqlServerConnectionString);
        Assert.Empty(publishers);

        // In the no-lease architecture there are no lease columns — remaining messages
        // are immediately available for the next publisher.
        var remaining = await SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString);
        _output.WriteLine($"{remaining} messages remain, all immediately available");

        // Start publisher B — should pick up remaining messages immediately
        var (hostB, _) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.SqlServerConnectionString, _infra.EventHubConnectionString,
            useSqlServer: true);
        var startTime = DateTime.UtcNow;

        try
        {
            await hostB.StartAsync();

            await OutboxTestHelper.WaitUntilAsync(
                () => SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(20), message: "B should process remaining messages quickly");

            var elapsed = DateTime.UtcNow - startTime;
            _output.WriteLine($"Publisher B drained remaining messages in {elapsed.TotalSeconds:F1}s");

            // Should be much less than the 120s lease duration
            Assert.True(elapsed.TotalSeconds < 30,
                $"Messages should be picked up within seconds, not waiting for 120s lease expiry. Took {elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            await hostB.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            hostB.Dispose();
        }
    }
}
