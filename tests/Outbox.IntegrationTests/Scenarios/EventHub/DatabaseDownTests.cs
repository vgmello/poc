// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

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
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var connFactory = new ToggleableConnectionFactory(_infra.ConnectionString);
        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString,
            connectionFactory: connFactory);

        try
        {
            await host.StartAsync();

            // Insert and publish some messages successfully
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, eventHub, partitionKey);
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
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, eventHub, partitionKey);

            // Wait for new messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "Messages should drain after DB recovery");

            // Verify messages arrived at broker
            var consumed = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 30, TimeSpan.FromSeconds(10), partitionKey);
            Assert.True(consumed.Count >= 30,
                $"Expected at least 30 messages consumed, got {consumed.Count}");

            // Verify publisher is still registered
            var publishers = await OutboxTestHelper.GetPublisherIdsAsync(_infra.ConnectionString);
            Assert.Single(publishers);
        }
        finally
        {
            connFactory.SetFailing(false);
            await host.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }
}
