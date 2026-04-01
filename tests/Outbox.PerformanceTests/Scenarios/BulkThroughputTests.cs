using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Outbox.PerformanceTests.Fixtures;
using Outbox.PerformanceTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.PerformanceTests.Scenarios;

[Collection(PerformanceCollection.Name)]
public class BulkThroughputTests
{
    private const int TotalMessages = 1_000_000;
    private const int StabilizationDelayMs = 15_000;

    private readonly PerformanceFixture _fixture;
    private readonly ITestOutputHelper _output;

    public BulkThroughputTests(PerformanceFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory(Timeout = 600_000)] // 10 minute timeout per combination
    [MemberData(nameof(TestMatrix.AllCombinations), MemberType = typeof(TestMatrix))]
    public async Task BulkDrain(TestCombination combo)
    {
        var storeConnStr = combo.Store == StoreType.PostgreSql
            ? _fixture.PostgreSqlConnectionString
            : _fixture.SqlServerConnectionString;

        // Cleanup
        await CleanupHelper.CleanupAsync(combo.Store, storeConnStr);

        // Pre-seed messages
        _output.WriteLine($"[Bulk] {combo.Label}: Seeding {TotalMessages:N0} messages...");
        var seedSw = Stopwatch.StartNew();
        await MessageProducer.BulkSeedAsync(combo.Store, storeConnStr, TotalMessages);
        seedSw.Stop();
        _output.WriteLine($"[Bulk] {combo.Label}: Seeding complete in {seedSw.Elapsed:mm\\:ss}");

        // Verify seed count
        var seeded = await CleanupHelper.GetPendingCountAsync(combo.Store, storeConnStr);
        Assert.Equal(TotalMessages, seeded);

        // Start metrics collector
        using var metrics = new MetricsCollector();

        // Start publishers
        var hosts = new List<IHost>();
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

        var sw = Stopwatch.StartNew();

        // Start all publisher hosts
        foreach (var host in hosts)
            await host.StartAsync();

        // Wait for stabilization (partition rebalancing) if multi-publisher
        if (combo.PublisherCount > 1)
        {
            _output.WriteLine($"[Bulk] {combo.Label}: Waiting {StabilizationDelayMs}ms for rebalancing...");
            await Task.Delay(StabilizationDelayMs);
        }

        // Wait for all messages to drain
        var timeout = TimeSpan.FromMinutes(8);
        var pollInterval = TimeSpan.FromSeconds(2);
        var lastLog = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            var pending = await CleanupHelper.GetPendingCountAsync(combo.Store, storeConnStr);

            if (lastLog.Elapsed > TimeSpan.FromSeconds(10))
            {
                var published = TotalMessages - pending;
                var rate = published / sw.Elapsed.TotalSeconds;
                _output.WriteLine($"[Bulk] {combo.Label}: {published:N0}/{TotalMessages:N0} " +
                                  $"({100.0 * published / TotalMessages:F1}%) — {rate:N0} msg/sec");
                lastLog.Restart();
            }

            if (pending == 0) break;
            await Task.Delay(pollInterval);
        }

        sw.Stop();

        // Stop publishers
        foreach (var host in hosts)
            await host.StopAsync(TimeSpan.FromSeconds(10));
        foreach (var host in hosts)
            host.Dispose();

        // Verify drain
        var remaining = await CleanupHelper.GetPendingCountAsync(combo.Store, storeConnStr);
        Assert.Equal(0, remaining);

        // Collect results
        var result = new BulkResult(
            combo,
            TotalMessages,
            sw.Elapsed,
            metrics.GetHistogramStats("outbox.poll.duration"),
            metrics.GetHistogramStats("outbox.publish.duration"),
            metrics.GetHistogramStats("outbox.poll.batch_size"));

        PerfReportWriter.WriteBulkToConsole(result, _output);

        _fixture.AddBulkResult(result);
    }
}
