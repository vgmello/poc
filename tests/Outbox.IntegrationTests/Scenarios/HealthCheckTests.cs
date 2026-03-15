using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

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
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

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
            var topic = OutboxTestHelper.UniqueTopic("health");
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, topic, "key-1");
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
            host.Dispose();
        }
    }
}
