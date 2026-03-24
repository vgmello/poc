// Copyright (c) OrgName. All rights reserved.

using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

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
    public async Task CircuitOpen_DoesNotIncrementRetryCount_MessagesPublishAfterRecovery()
    {
        var topic = OutboxTestHelper.UniqueTopic("circuit-retry");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.MaxRetryCount = 10; // High so half-open probes don't exhaust retries
                o.CircuitBreakerFailureThreshold = 2; // Open after 2 failures
                o.CircuitBreakerOpenDurationSeconds = 5;
            });

        try
        {
            await host.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(2)); // Let publisher register

            // Block broker
            transport.SetFailing(true);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");

            // Wait for circuit to open (2 failures -> circuit opens)
            await Task.Delay(TimeSpan.FromSeconds(5));

            // Check retry counts — should be bounded by initial failures (2) + possible half-open probes.
            // With CircuitBreakerOpenDurationSeconds=5, at most 1 half-open probe may have fired.
            var retryCounts = await OutboxTestHelper.GetRetryCountsAsync(_infra.ConnectionString);

            foreach (var (seq, retryCount) in retryCounts)
            {
                Assert.True(retryCount <= 3,
                    $"Message {seq} has retry_count={retryCount}, expected <= 3 (2 initial failures + at most 1 half-open probe)");
            }

            _output.WriteLine($"Retry counts during circuit open: {string.Join(", ", retryCounts.Select(r => r.RetryCount))}");

            // Wait through a few more circuit cycles with broker still down
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Re-check: retry counts should be well below MaxRetryCount (10)
            // Only actual send attempts (initial failures + half-open probes) increment, not circuit-open releases
            retryCounts = await OutboxTestHelper.GetRetryCountsAsync(_infra.ConnectionString);

            foreach (var (seq, retryCount) in retryCounts)
            {
                Assert.True(retryCount < 10,
                    $"Message {seq} has retry_count={retryCount}, should be < MaxRetryCount (10). Circuit open should NOT burn retries.");
            }

            // No messages should be dead-lettered
            Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));

            // Restore broker
            transport.SetFailing(false);

            // Wait for messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should publish after broker recovery");

            // Assert: all messages published, none dead-lettered
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 10, TimeSpan.FromSeconds(10));
            Assert.True(consumed.Count >= 10, $"Expected at least 10 messages, got {consumed.Count}");
            Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));

            _output.WriteLine("All messages published after recovery, none dead-lettered");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
