// Copyright (c) OrgName. All rights reserved.

using Microsoft.Data.SqlClient;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.SqlServer;

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
    public async Task GracefulShutdown_ReleasesLeases_NewPublisherPicksUpImmediately()
    {
        var topic = OutboxTestHelper.UniqueTopic("ss-graceful");
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);

        // Use long lease so we can tell the difference between "released" and "expired"
        var (hostA, _) = SqlServerTestHelper.BuildPublisherHost(
            _infra.SqlServerConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.LeaseDurationSeconds = 120;
                o.PartitionGracePeriodSeconds = 180;
            });

        await hostA.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3)); // Let A register and claim partitions

        // Insert messages
        await SqlServerTestHelper.InsertMessagesAsync(_infra.SqlServerConnectionString, 30, topic, "key-1");
        await Task.Delay(TimeSpan.FromSeconds(2)); // Let A begin processing

        // Graceful shutdown
        await hostA.StopAsync();
        hostA.Dispose();
        _output.WriteLine("Host A stopped gracefully");

        // Assert: producer unregistered
        var producers = await SqlServerTestHelper.GetProducerIdsAsync(_infra.SqlServerConnectionString);
        Assert.Empty(producers);

        // Assert: leased messages have LeasedUntilUtc = NULL (released, not waiting for 120s expiry)
        var remaining = await SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString);

        if (remaining > 0)
        {
            await using var conn = new SqlConnection(_infra.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.Outbox WHERE LeasedUntilUtc IS NOT NULL", conn);
            var leased = (long)(int)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(0, leased); // All leases should be released
            _output.WriteLine($"{remaining} messages remain but all leases released");
        }

        // Start publisher B — should pick up remaining messages immediately (not waiting 120s)
        var (hostB, _) = SqlServerTestHelper.BuildPublisherHost(
            _infra.SqlServerConnectionString, _infra.BootstrapServers);
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
            hostB.Dispose();
        }
    }
}
