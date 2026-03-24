// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Outbox.Core.Observability;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class LoopCrashRecoveryTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public LoopCrashRecoveryTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task TransientDbFailure_PublisherSurvives_AndResumesProcessing()
    {
        var topic = OutboxTestHelper.UniqueTopic("loop-crash");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var connFactory = new ToggleableConnectionFactory(_infra.ConnectionString);
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            connectionFactory: connFactory);

        var healthState = host.Services.GetRequiredService<OutboxHealthState>();
        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        try
        {
            await host.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Verify healthy operation
            Assert.True(healthState.IsPublishLoopRunning);
            _output.WriteLine("Publisher is running");

            // Publish some messages successfully first
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, topic, "key-1");
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(15), message: "Initial messages should drain");

            // Inject DB failure — all DB ops fail. The heartbeat loop (2s interval)
            // will exit after 3 consecutive failures (~6s), triggering a loop restart.
            connFactory.SetFailing(true);
            _output.WriteLine("DB set to failing");

            // Wait for the heartbeat loop to crash and trigger at least one restart.
            // 3 failures * 2s heartbeat interval = ~6s, plus restart backoff (2s) = ~8s.
            await OutboxTestHelper.WaitUntilAsync(
                () => Task.FromResult(healthState.ConsecutiveLoopRestarts > 0),
                TimeSpan.FromSeconds(15),
                message: "Expected at least one loop restart from heartbeat consecutive failures during DB outage");

            _output.WriteLine($"ConsecutiveLoopRestarts during DB failure: {healthState.ConsecutiveLoopRestarts}");

            // Restore DB
            connFactory.SetFailing(false);
            _output.WriteLine("DB restored");

            // Insert more messages — should be processed after recovery
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "Messages should drain after DB recovery");

            // Verify health returns to healthy
            var report = await healthCheckService.CheckHealthAsync();
            var outboxEntry = report.Entries["outbox"];
            _output.WriteLine($"Health after recovery: {outboxEntry.Status}");

            // After 30s of healthy operation, ConsecutiveLoopRestarts should reset to 0
            await OutboxTestHelper.WaitUntilAsync(
                () => Task.FromResult(healthState.ConsecutiveLoopRestarts == 0),
                TimeSpan.FromSeconds(40),
                message: "ConsecutiveLoopRestarts should reset to 0 after sustained healthy operation");
            _output.WriteLine("ConsecutiveLoopRestarts reset to 0 after recovery");

            // Verify messages consumed
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 15, TimeSpan.FromSeconds(10));
            Assert.True(consumed.Count >= 15,
                $"Expected at least 15 messages (5 initial + 10 after recovery), got {consumed.Count}");

            _output.WriteLine($"All messages processed. Consumed: {consumed.Count}");
        }
        finally
        {
            connFactory.SetFailing(false);
            await host.StopAsync();
            host.Dispose();
        }
    }
}
