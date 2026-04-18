// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class HealthCheckTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public HealthCheckTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task HealthCheck_ReportsCorrectStates()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString);

        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        try
        {
            await host.StartAsync();

            // Phase 1: Healthy
            await Task.Delay(TimeSpan.FromSeconds(5));
            var report = await healthCheckService.CheckHealthAsync();
            var outboxEntry = report.Entries["outbox"];

            _output.WriteLine($"Phase 1 - Status: {outboxEntry.Status}, Description: {outboxEntry.Description}");
            Assert.Equal(HealthStatus.Healthy, outboxEntry.Status);

            // Phase 2: Degraded (open circuit breaker)
            transport.SetFailing(true);
            var partitionKey = $"pk-{Guid.NewGuid():N}";
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, eventHub, partitionKey);
            await Task.Delay(TimeSpan.FromSeconds(8)); // Wait for circuit to open

            report = await healthCheckService.CheckHealthAsync();
            outboxEntry = report.Entries["outbox"];
            _output.WriteLine($"Phase 2 - Status: {outboxEntry.Status}, Description: {outboxEntry.Description}");
            Assert.Equal(HealthStatus.Degraded, outboxEntry.Status);

            // Phase 3: Recover
            transport.SetFailing(false);
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var r = await healthCheckService.CheckHealthAsync();

                return r.Entries["outbox"].Status == HealthStatus.Healthy;
            }, TimeSpan.FromSeconds(30), message: "Health should return to Healthy");

            _output.WriteLine("Phase 3 - Recovered to Healthy");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }

    [Fact]
    public async Task HealthCheck_ReportsUnhealthy_WhenHeartbeatStale()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var connFactory = new ToggleableConnectionFactory(_infra.ConnectionString);

        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString,
            configureOptions: o =>
            {
                OutboxTestHelper.FastTestOptions(o);
                o.HeartbeatIntervalMs = 1_000; // Fast heartbeat
                o.HeartbeatTimeoutSeconds = 4; // 4s timeout (>= 3x interval)
            },
            connectionFactory: connFactory);

        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        try
        {
            await host.StartAsync();

            // Phase 1: Verify Healthy first
            await Task.Delay(TimeSpan.FromSeconds(3));
            var report = await healthCheckService.CheckHealthAsync();
            var outboxEntry = report.Entries["outbox"];
            _output.WriteLine($"Phase 1 - Status: {outboxEntry.Status}");
            Assert.Equal(HealthStatus.Healthy, outboxEntry.Status);

            // Phase 2: Block DB — heartbeat will go stale
            connFactory.SetFailing(true);
            _output.WriteLine("DB blocked — waiting for heartbeat to go stale");

            // Wait for health check to report Unhealthy
            // Heartbeat timeout is 4s, so after ~5-6s the health check should detect staleness
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var r = await healthCheckService.CheckHealthAsync();
                var status = r.Entries["outbox"].Status;
                _output.WriteLine($"  Health: {status}");

                return status == HealthStatus.Unhealthy;
            }, TimeSpan.FromSeconds(15), message: "Health should report Unhealthy after stale heartbeat");

            _output.WriteLine("Phase 2 - Unhealthy detected");

            // Phase 3: Restore DB — should recover
            connFactory.SetFailing(false);

            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var r = await healthCheckService.CheckHealthAsync();

                return r.Entries["outbox"].Status == HealthStatus.Healthy;
            }, TimeSpan.FromSeconds(30), message: "Health should recover after DB restored");

            _output.WriteLine("Phase 3 - Recovered to Healthy");
        }
        finally
        {
            connFactory.SetFailing(false);
            transport.Reset();
            await host.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }
}
