// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.SqlServer;

[Collection(InfrastructureCollection.Name)]
public class SqlServerIntermittentFailureTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public SqlServerIntermittentFailureTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task IntermittentFailures_RetryCountIncrements_MessagesEventuallyPublished()
    {
        var topic = OutboxTestHelper.UniqueTopic("ss-intermittent");
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);

        // With intermittent failures (fail 2 out of 3 calls), messages accumulate retries
        // but eventually publish on the succeeding call. Circuit threshold is high enough
        // that it never opens (max 2 consecutive failures before a success).
        var (host, transport) = SqlServerTestHelper.BuildPublisherHost(
            _infra.SqlServerConnectionString, _infra.BootstrapServers,
            o =>
            {
                OutboxTestHelper.FastTestOptions(o);
                // MaxRetryCount must be > CircuitBreakerFailureThreshold per validator
                o.MaxRetryCount = 10;
                o.CircuitBreakerFailureThreshold = 5;
            });

        // Fail 2 out of 3 calls (succeed every 3rd)
        transport.SetIntermittent(3);

        try
        {
            await SqlServerTestHelper.InsertMessagesAsync(_infra.SqlServerConnectionString, 10, topic, "key-1");
            await host.StartAsync();

            // Wait for all messages to be published (retries push them through on succeeding calls)
            await OutboxTestHelper.WaitUntilAsync(
                () => SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All messages should be published through retries");

            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 10, TimeSpan.FromSeconds(15));

            _output.WriteLine($"Consumed: {consumed.Count}");

            // All messages should be published (duplicates possible due to at-least-once)
            Assert.True(consumed.Count >= 10,
                $"All 10 messages should be published, got {consumed.Count}");

            // Key assertion: outbox is fully drained
            Assert.Equal(0, await SqlServerTestHelper.GetOutboxCountAsync(_infra.SqlServerConnectionString));
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
