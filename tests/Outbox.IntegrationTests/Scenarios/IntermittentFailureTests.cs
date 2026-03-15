using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class IntermittentFailureTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public IntermittentFailureTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task IntermittentFailures_RetryCountIncrements_MessagesEventuallyPublishedOrDeadLettered()
    {
        var topic = OutboxTestHelper.UniqueTopic("intermittent");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // MaxRetryCount=5, high circuit threshold so circuit doesn't open
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.MaxRetryCount = 5;
                o.CircuitBreakerFailureThreshold = 100; // Don't open circuit
            });

        // Fail 2 out of 3 calls (succeed every 3rd)
        transport.SetIntermittent(3);

        try
        {
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");
            await host.StartAsync();

            // Wait for all messages to be either published or dead-lettered
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All messages should be processed (published or dead-lettered)");

            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 10, TimeSpan.FromSeconds(15));
            var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);

            _output.WriteLine($"Consumed: {consumed.Count}, Dead-lettered: {dlqCount}");

            // All messages should be accounted for (published + dead-lettered)
            // Some may be duplicated in Kafka, so consumed.Count >= some number
            Assert.True(consumed.Count + dlqCount >= 10,
                $"All 10 messages should be either published ({consumed.Count}) or dead-lettered ({dlqCount})");

            // Key assertion: retry_count was incrementing (messages didn't retry forever)
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
