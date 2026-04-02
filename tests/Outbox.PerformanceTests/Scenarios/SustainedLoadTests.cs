using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Outbox.PerformanceTests.Fixtures;
using Outbox.PerformanceTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.PerformanceTests.Scenarios;

[Collection(PerformanceCollection.Name)]
public class SustainedLoadTests
{
    private static readonly TimeSpan TestDuration = TimeSpan.FromMinutes(5);
    private const int StabilizationDelayMs = 15_000;

    private static int GetTargetRate(TestCombination combo) => (combo.Store, combo.Transport) switch
    {
        (StoreType.PostgreSql, TransportType.Redpanda) => 1_000,
        (StoreType.PostgreSql, TransportType.EventHub) => 500,
        (StoreType.SqlServer, TransportType.Redpanda) => 1_000,
        (StoreType.SqlServer, TransportType.EventHub) => 1_000,
        _ => 100
    };

    private readonly PerformanceFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SustainedLoadTests(PerformanceFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory(Timeout = 600_000)] // 10 minute timeout per combination
    [MemberData(nameof(TestMatrix.AllCombinations), MemberType = typeof(TestMatrix))]
    public async Task SustainedRate(TestCombination combo)
    {
        var targetRate = GetTargetRate(combo);
        var storeConnStr = combo.Store == StoreType.PostgreSql
            ? _fixture.PostgreSqlConnectionString
            : _fixture.SqlServerConnectionString;

        // Cleanup
        await CleanupHelper.CleanupAsync(combo.Store, storeConnStr);

        // Start metrics collector
        using var metrics = new MetricsCollector(pendingSampleInterval: TimeSpan.FromSeconds(5));

        // Start publishers
        var hosts = new List<IHost>();
        try
        {
            for (var i = 0; i < combo.PublisherCount; i++)
            {
                var host = PublisherHostBuilder.Build(
                    combo,
                    _fixture.PostgreSqlConnectionString,
                    _fixture.SqlServerConnectionString,
                    _fixture.BootstrapServers,
                    _fixture.EventHubConnectionString);
                hosts.Add(host);
            }

            foreach (var host in hosts)
                await host.StartAsync();

            // Wait for stabilization if multi-publisher
            if (combo.PublisherCount > 1)
            {
                _output.WriteLine($"[Sustained] {combo.Label}: Waiting {StabilizationDelayMs}ms for rebalancing...");
                await Task.Delay(StabilizationDelayMs);
            }

            // Start sustained producer
            using var producerCts = new CancellationTokenSource(TestDuration);
            var producerTask = MessageProducer.ProduceAtRateAsync(
                combo.Store, storeConnStr, targetRate, producerCts.Token);

            // Monitor progress
            var sw = Stopwatch.StartNew();
            var lastLog = Stopwatch.StartNew();
            long peakPending = 0;

            while (sw.Elapsed < TestDuration)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));

                var pending = await CleanupHelper.GetPendingCountAsync(combo.Store, storeConnStr);
                if (pending > peakPending) peakPending = pending;

                if (lastLog.Elapsed > TimeSpan.FromSeconds(15))
                {
                    _output.WriteLine($"[Sustained] {combo.Label}: pending={pending:N0} peak={peakPending:N0} " +
                                      $"published={metrics.GetCounter("outbox.messages.published"):N0} " +
                                      $"elapsed={sw.Elapsed:mm\\:ss}");
                    lastLog.Restart();
                }
            }

            // Wait for producer to finish
#pragma warning disable S108 // Empty catch is intentional — cancellation is expected here
            try { await producerTask; }
            catch (OperationCanceledException) { }
#pragma warning restore S108

            // Give publishers a few seconds to drain remaining
            await Task.Delay(TimeSpan.FromSeconds(10));

            var finalPending = await CleanupHelper.GetPendingCountAsync(combo.Store, storeConnStr);
            if (finalPending > peakPending) peakPending = finalPending;

            // Calculate drain rate
            var totalPublished = metrics.GetCounter("outbox.messages.published");
            var avgDrainRate = totalPublished / TestDuration.TotalSeconds;

            // Collect results
            var result = new SustainedResult(
                combo,
                targetRate,
                TestDuration,
                avgDrainRate,
                peakPending,
                finalPending,
                metrics.GetHistogramStats("outbox.poll.duration"),
                metrics.GetHistogramStats("outbox.publish.duration"),
                metrics.GetPendingSnapshots());

            PerfReportWriter.WriteSustainedToConsole(result, _output);

            // Verify at least some messages were published during the sustained run
            Assert.True(totalPublished > 0, "Expected at least one message to be published during sustained load test");

            _fixture.AddSustainedResult(result);
        }
        finally
        {
            foreach (var host in hosts)
            {
                try { await host.StopAsync(TimeSpan.FromSeconds(5)); }
                catch { /* best effort */ }
                host.Dispose();
            }
        }
    }
}
