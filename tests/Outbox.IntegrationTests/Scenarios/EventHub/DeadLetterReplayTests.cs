// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Abstractions;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class DeadLetterReplayTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public DeadLetterReplayTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task DeadLetterReplay_MovesBackToOutbox_AndPublishes()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Phase 1: Create dead-lettered messages using non-transient failures so
        // attempts are consumed and DLQ happens inline after MaxPublishAttempts.
        var (host1, transport1) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString,
            o =>
            {
                o.MaxPublishAttempts = 2;
                o.CircuitBreakerFailureThreshold = 10; // High so circuit never opens
                o.RetryBackoffBaseMs = 50;
                o.RetryBackoffMaxMs = 100;
            });
        transport1.SetSimulatedFailuresTransient(false);
        transport1.SetFailing(true);

        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 3, eventHub, partitionKey);
        await host1.StartAsync();

        // Wait for messages to be dead-lettered
        await OutboxTestHelper.WaitUntilAsync(
            () => OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result >= 3),
            TimeSpan.FromSeconds(30), message: "Messages should be dead-lettered");

        await host1.StopAsync();
        host1.Dispose();

        var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);
        _output.WriteLine($"Dead-lettered messages: {dlqCount}");
        Assert.True(dlqCount >= 3);

        // Phase 2: Replay dead-lettered messages
        var (host2, transport2) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString);

        var dlManager = host2.Services.GetRequiredService<IDeadLetterManager>();

        // Get dead-lettered sequence numbers
        var deadLettered = await dlManager.GetAsync(100, 0, CancellationToken.None);
        Assert.True(deadLettered.Count >= 3);

        // Replay them using the dead-letter PK (DeadLetterSeq) for precise targeting
        await dlManager.ReplayAsync(
            deadLettered.Select(d => d.DeadLetterSeq).ToList(),
            CancellationToken.None);

        // Assert: moved from dead letter back to outbox
        Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));
        Assert.True(await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString) >= 3);

        // Note: retry_count column was removed in the in-memory retry rework.
        // Replayed messages start fresh with a full attempt budget (in-memory, not persisted).
        _output.WriteLine("Replayed messages are back in the outbox with fresh in-memory attempt budget");

        // Phase 3: Start publisher — should publish the replayed messages
        try
        {
            await host2.StartAsync();

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(20), message: "Replayed messages should be published");

            var consumed = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 3, TimeSpan.FromSeconds(5), partitionKey);
            Assert.True(consumed.Count >= 3, $"Expected at least 3 replayed messages, got {consumed.Count}");

            _output.WriteLine($"Replayed messages published: {consumed.Count}");
        }
        finally
        {
            await host2.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host2.Dispose();
        }
    }
}
