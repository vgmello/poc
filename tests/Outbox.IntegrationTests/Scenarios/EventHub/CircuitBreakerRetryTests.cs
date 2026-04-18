// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class CircuitBreakerRetryTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public CircuitBreakerRetryTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task CircuitOpen_TransientFailures_NoDlq_MessagesPublishAfterRecovery()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Failures are transient by default (broker outage simulation).
        // Transient failures open the circuit but never burn attempts and never DLQ.
        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString,
            o =>
            {
                o.MaxPublishAttempts = 5;
                o.CircuitBreakerFailureThreshold = 2; // Open after 2 consecutive transient failures
                o.CircuitBreakerOpenDurationSeconds = 5;
            });

        // Default: _simulatedFailuresAreTransient = true — circuit opens, no DLQ.

        try
        {
            await host.StartAsync();

            // Wait for publisher to register and claim partitions (rebalance loop delays before first action)
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
                return owners.Values.Any(v => v != null);
            }, TimeSpan.FromSeconds(10), message: "Publisher should claim partitions");

            // Block broker
            transport.SetFailing(true);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, eventHub, partitionKey);

            // Wait for circuit to open (2 transient failures) then a few more cycles
            await Task.Delay(TimeSpan.FromSeconds(12));

            // Key assertion: no rows in DLQ during circuit-open period.
            // Transient failures never exhaust attempts, so nothing is dead-lettered.
            Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));

            // Messages should still be waiting in the outbox (not published, not dead-lettered)
            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            Assert.True(pending > 0, $"Messages should still be pending in outbox, got {pending}");

            _output.WriteLine($"During outage: pending={pending}, dlq=0 (circuit open, no DLQ — correct)");

            // Restore broker
            transport.SetFailing(false);

            // Wait for messages to drain (circuit half-opens, probes succeed, drains)
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should publish after broker recovery");

            // Assert: all messages published, none dead-lettered
            var consumed = await EventHubTestHelper.ConsumeMessagesAsync(
                _infra.EventHubConnectionString, eventHub, 10, TimeSpan.FromSeconds(10), partitionKey);
            Assert.True(consumed.Count >= 10, $"Expected at least 10 messages, got {consumed.Count}");
            Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));

            _output.WriteLine("All messages published after recovery, none dead-lettered");
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
