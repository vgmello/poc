// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class MultiPublisherRebalanceTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public MultiPublisherRebalanceTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task ScaleUp_PartitionsRedistributedFairly()
    {
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);
        var eventHub = EventHubTestHelper.CheckoutHub();

        // Start publisher A — should own all 64 partitions
        var (hostA, _) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString);
        await hostA.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Count(v => v != null) == 64;
        }, TimeSpan.FromSeconds(15), message: "A should own all 64 partitions");

        var publishers = await OutboxTestHelper.GetPublisherIdsAsync(_infra.ConnectionString);
        _output.WriteLine($"A owns 64 partitions. PublisherId: {publishers[0]}");

        // Start publisher B (same hub)
        var (hostB, _) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString);
        await hostB.StartAsync();

        // Wait for rebalance — should be roughly 32/32
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
            var grouped = owners.Values.Where(v => v != null).GroupBy(v => v).ToList();

            return grouped.Count == 2 && grouped.All(g => g.Count() >= 24); // ~32 each, allowing some variance
        }, TimeSpan.FromSeconds(30), message: "Partitions should be split roughly 32/32");

        var ownersAfter = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
        var distribution = ownersAfter.Values.Where(v => v != null).GroupBy(v => v)
            .Select(g => $"{g.Key}: {g.Count()} partitions").ToList();
        _output.WriteLine($"After rebalance: {string.Join(", ", distribution)}");

        // Insert messages — both should process
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, eventHub);
        await OutboxTestHelper.WaitUntilAsync(
            () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
            TimeSpan.FromSeconds(20), message: "All messages should drain with 2 publishers");

        // Scale down: stop B
        await hostB.StopAsync();
        hostB.Dispose();

        // A should reclaim all partitions
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Count(v => v != null) == 64
                   && owners.Values.Distinct().Count(v => v != null) == 1;
        }, TimeSpan.FromSeconds(30), message: "A should reclaim all 64 partitions after B stops");

        _output.WriteLine("A reclaimed all partitions after B stopped");

        try
        {
            Assert.True(true, "Test completed without exceptions");
        }
        finally
        {
            await hostA.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            hostA.Dispose();
        }
    }
}
