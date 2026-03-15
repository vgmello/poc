using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class DatabaseDownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public DatabaseDownTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task DatabaseDown_PublisherSurvives_AndRecoversAutomatically()
    {
        var topic = OutboxTestHelper.UniqueTopic("db-down");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var connFactory = new ToggleableConnectionFactory(_infra.ConnectionString);
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            connectionFactory: connFactory);

        try
        {
            await host.StartAsync();

            // Insert and publish some messages successfully
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(15), message: "Initial messages should drain");

            // Block DB
            connFactory.SetFailing(true);
            _output.WriteLine("DB set to failing");

            // Wait a bit — publisher should survive (no crash, no unhandled exception)
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Restore DB
            connFactory.SetFailing(false);
            _output.WriteLine("DB restored");

            // Insert more messages
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, topic, "key-1");

            // Wait for new messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "Messages should drain after DB recovery");

            // Verify messages arrived at broker
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 30, TimeSpan.FromSeconds(10));
            Assert.True(consumed.Count >= 30,
                $"Expected at least 30 messages consumed, got {consumed.Count}");

            // Verify producer is still registered
            var producers = await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString);
            Assert.Single(producers);
        }
        finally
        {
            connFactory.SetFailing(false);
            await host.StopAsync();
            host.Dispose();
        }
    }
}
