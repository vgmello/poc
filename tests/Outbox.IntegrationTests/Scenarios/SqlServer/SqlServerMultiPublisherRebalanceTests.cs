// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.SqlServer;

[Collection(InfrastructureCollection.Name)]
public class SqlServerMultiPublisherRebalanceTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public SqlServerMultiPublisherRebalanceTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task ScaleUp_PartitionsRedistributedFairly()
    {
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);
        var topic = OutboxTestHelper.UniqueTopic("ss-rebalance");

        // Start publisher A — should own all 32 partitions
        var (hostA, _) = SqlServerTestHelper.BuildPublisherHost(
            _infra.SqlServerConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await SqlServerTestHelper.GetPartitionOwnersAsync(_infra.SqlServerConnectionString);

            return owners.Values.Count(v => v != null) == 32;
        }, TimeSpan.FromSeconds(15), message: "A should own all 32 partitions");

        var publishers = await SqlServerTestHelper.GetPublisherIdsAsync(_infra.SqlServerConnectionString);
        _output.WriteLine($"A owns 32 partitions. PublisherId: {publishers[0]}");

        // Start publisher B
        var (hostB, _) = SqlServerTestHelper.BuildPublisherHost(
            _infra.SqlServerConnectionString, _infra.BootstrapServers);
        await hostB.StartAsync();

        // Wait for rebalance — should be roughly 16/16
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await SqlServerTestHelper.GetPartitionOwnersAsync(_infra.SqlServerConnectionString);
            var grouped = owners.Values.Where(v => v != null).GroupBy(v => v).ToList();

            return grouped.Count == 2 && grouped.All(g => g.Count() >= 12); // ~16 each, allowing some variance
        }, TimeSpan.FromSeconds(30), message: "Partitions should be split roughly 16/16");

        var ownersAfter = await SqlServerTestHelper.GetPartitionOwnersAsync(_infra.SqlServerConnectionString);
        var distribution = ownersAfter.Values.Where(v => v != null).GroupBy(v => v)
            .Select(g => $"{g.Key}: {g.Count()} partitions").ToList();
        _output.WriteLine($"After rebalance: {string.Join(", ", distribution)}");

        // Insert messages — both should process
        await SqlServerTestHelper.InsertMessagesAsync(_infra.SqlServerConnectionString, 50, topic);
        await OutboxTestHelper.WaitUntilAsync(
            () => SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString).ContinueWith(t => t.Result == 0),
            TimeSpan.FromSeconds(20), message: "All messages should drain with 2 publishers");

        // Scale down: stop B
        await hostB.StopAsync();
        hostB.Dispose();

        // A should reclaim all partitions
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await SqlServerTestHelper.GetPartitionOwnersAsync(_infra.SqlServerConnectionString);

            return owners.Values.Count(v => v != null) == 32
                   && owners.Values.Distinct().Count(v => v != null) == 1;
        }, TimeSpan.FromSeconds(30), message: "A should reclaim all 32 partitions after B stops");

        _output.WriteLine("A reclaimed all partitions after B stopped");

        await hostA.StopAsync();
        hostA.Dispose();

        Assert.True(true, "Test completed without exceptions");
    }
}
