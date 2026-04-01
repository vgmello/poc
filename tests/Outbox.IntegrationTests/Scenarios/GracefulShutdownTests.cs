// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class GracefulShutdownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public GracefulShutdownTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task GracefulShutdown_UnregistersPublisher_NewPublisherPicksUpImmediately()
    {
        var topic = OutboxTestHelper.UniqueTopic("graceful");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (hostA, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.PartitionGracePeriodSeconds = 180;
            });

        await hostA.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3)); // Let A register and claim partitions

        // Insert messages
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 30, topic, "key-1");
        await Task.Delay(TimeSpan.FromSeconds(2)); // Let A begin processing

        // Graceful shutdown
        await hostA.StopAsync();
        hostA.Dispose();
        _output.WriteLine("Host A stopped gracefully");

        // Assert: publisher unregistered
        var publishers = await OutboxTestHelper.GetPublisherIdsAsync(_infra.ConnectionString);
        Assert.Empty(publishers);

        // In the no-lease architecture there are no lease columns — remaining messages
        // are immediately available for the next publisher.
        var remaining = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
        _output.WriteLine($"{remaining} messages remain, all immediately available");

        // Start publisher B — should pick up remaining messages immediately
        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        var startTime = DateTime.UtcNow;

        try
        {
            await hostB.StartAsync();

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
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
            hostB.Dispose();
        }
    }
}
