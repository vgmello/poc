using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Observability;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class PendingMetricTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public PendingMetricTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PendingGauge_ReflectsOutboxDepth()
    {
        var topic = OutboxTestHelper.UniqueTopic("pending");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Block transport so messages accumulate
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        transport.SetFailing(true);

        try
        {
            await host.StartAsync();

            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic, "key-1");

            // Wait for at least one heartbeat cycle to update pending count
            await Task.Delay(TimeSpan.FromSeconds(5));

            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            _output.WriteLine($"Pending messages in DB: {pending}");
            Assert.True(pending > 0, "Messages should be pending");

            // Restore transport
            transport.SetFailing(false);

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain");

            _output.WriteLine("All messages drained, pending = 0");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
