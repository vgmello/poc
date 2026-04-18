// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

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
    public async Task IntermittentFailures_TransientDefault_MessagesEventuallyPublished()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Intermittent failures are transient by default (broker flap simulation).
        // Transient failures do NOT burn publish attempts — messages will eventually
        // all succeed. The circuit breaker may open briefly but will recover.
        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString,
            o =>
            {
                OutboxTestHelper.FastTestOptions(o);
                o.MaxPublishAttempts = 10;
                o.CircuitBreakerFailureThreshold = 5;
                o.CircuitBreakerOpenDurationSeconds = 3; // Recover quickly
            });

        // Fail 2 out of 3 calls (succeed every 3rd)
        transport.SetIntermittent(3);

        try
        {
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, eventHub, partitionKey);
            await host.StartAsync();

            // Wait for all messages to be published (retries push them through on succeeding calls)
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All messages should be published through retries");

            var consumed = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 10, TimeSpan.FromSeconds(15), partitionKey);

            _output.WriteLine($"Consumed: {consumed.Count}");

            // All messages should be published (duplicates possible due to at-least-once)
            Assert.True(consumed.Count >= 10,
                $"All 10 messages should be published, got {consumed.Count}");

            // Key assertion: outbox is fully drained
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));
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
