using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Engine;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.Core.Tests;

internal sealed class TestMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new(options);
    public void Dispose() { }
}

public class OutboxPublisherServiceTests : IDisposable
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _optionsMonitor;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxInstrumentation _instrumentation;
    private readonly OutboxHealthState _healthState;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly OutboxPublisherOptions _options;

    public OutboxPublisherServiceTests()
    {
        _store = Substitute.For<IOutboxStore>();
        _transport = Substitute.For<IOutboxTransport>();
        _eventHandler = Substitute.For<IOutboxEventHandler>();
        _logger = NullLogger<OutboxPublisherService>.Instance;
        _instrumentation = new OutboxInstrumentation(new TestMeterFactory());
        _healthState = new OutboxHealthState();
        _appLifetime = Substitute.For<IHostApplicationLifetime>();

        _options = new OutboxPublisherOptions
        {
            BatchSize = 10,
            MaxRetryCount = 5,
            MinPollIntervalMs = 10,
            MaxPollIntervalMs = 100,
            HeartbeatIntervalMs = 100_000,
            RebalanceIntervalMs = 100_000,
            OrphanSweepIntervalMs = 100_000,
            DeadLetterSweepIntervalMs = 100_000,
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerOpenDurationSeconds = 30,
        };

        _optionsMonitor = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        _optionsMonitor.CurrentValue.Returns(_options);
    }

    public void Dispose()
    {
        _instrumentation.Dispose();
    }

    private OutboxPublisherService CreateService() =>
        new(_store, _transport, _eventHandler, _optionsMonitor, _logger,
            _instrumentation, _healthState, _appLifetime);

    private static OutboxMessage MakeMessage(
        long seq, string topic = "orders", string key = "key-1", int retryCount = 0) =>
        new(seq, topic, key, "OrderCreated", null, "{}", DateTimeOffset.UtcNow, 0, retryCount, DateTimeOffset.UtcNow);

    [Fact]
    public async Task RegistersAndUnregistersProducer()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>())
            .Returns("producer-1");

        // Return empty batches so publish loop just polls
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);
        // Wait for cancellation to allow ExecuteAsync to run
        try { await Task.Delay(350, CancellationToken.None); } catch { }
        await service.StopAsync(CancellationToken.None);

        await _store.Received(1).RegisterProducerAsync(Arg.Any<CancellationToken>());
        await _store.Received(1).UnregisterProducerAsync("producer-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_LeasesAndPublishesBatch()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>())
            .Returns("producer-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Return messages on first call, empty afterwards
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;
                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);
        try { await Task.Delay(350, CancellationToken.None); } catch { }
        await service.StopAsync(CancellationToken.None);

        // Both messages have same topic+key, so should be sent in one call
        await _transport.Received(1).SendAsync(
            "orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 2),
            Arg.Any<CancellationToken>());

        await _store.Received(1).DeletePublishedAsync(
            "producer-1",
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2),
            Arg.Any<CancellationToken>());

        // Health state should record successful publish
        Assert.NotEqual(DateTimeOffset.MinValue, _healthState.LastSuccessfulPublishUtc);
    }

    [Fact]
    public async Task PublishLoop_OnTransportFailure_ReleasesLeaseWithRetryIncrement()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>())
            .Returns("producer-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;
                return Array.Empty<OutboxMessage>();
            });

        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Kafka down"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);
        try { await Task.Delay(350, CancellationToken.None); } catch { }
        await service.StopAsync(CancellationToken.None);

        // BUG-2 fix: ReleaseLeaseAsync should be called with incrementRetry: true
        await _store.Received().ReleaseLeaseAsync(
            "producer-1",
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2),
            true, // incrementRetry
            Arg.Any<CancellationToken>());

        await _store.DidNotReceive().DeletePublishedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_PoisonMessages_AreDeadLettered()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>())
            .Returns("producer-1");

        // Poison message: RetryCount >= MaxRetryCount (5)
        var poisonMessage = MakeMessage(1, retryCount: 5);
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return new[] { poisonMessage };
                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);
        try { await Task.Delay(350, CancellationToken.None); } catch { }
        await service.StopAsync(CancellationToken.None);

        await _store.Received().DeadLetterAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _eventHandler.Received().OnMessageDeadLetteredAsync(
            poisonMessage, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_GroupsByTopicAndPartitionKey()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>())
            .Returns("producer-1");

        var messages = new[]
        {
            MakeMessage(1, "orders", "key-1"),
            MakeMessage(2, "orders", "key-2"),
            MakeMessage(3, "shipments", "key-1"),
        };

        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;
                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);
        try { await Task.Delay(350, CancellationToken.None); } catch { }
        await service.StopAsync(CancellationToken.None);

        // 3 different (topic, key) combos → 3 SendAsync calls
        await _transport.Received(3).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HealthState_ReportsLoopRunning()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>())
            .Returns("producer-1");
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Assert.False(_healthState.IsPublishLoopRunning);

        await service.StartAsync(cts.Token);
        try { await Task.Delay(100, CancellationToken.None); } catch { }

        Assert.True(_healthState.IsPublishLoopRunning);

        try { await Task.Delay(250, CancellationToken.None); } catch { }
        await service.StopAsync(CancellationToken.None);

        Assert.False(_healthState.IsPublishLoopRunning);
    }
}
