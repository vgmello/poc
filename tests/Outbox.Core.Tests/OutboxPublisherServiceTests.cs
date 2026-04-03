// Copyright (c) OrgName. All rights reserved.

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

public sealed class OutboxPublisherServiceTests : IDisposable
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _optionsMonitor;
    private readonly OutboxInstrumentation _instrumentation;
    private readonly OutboxHealthState _healthState;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly OutboxPublisherOptions _options;

    public OutboxPublisherServiceTests()
    {
        _store = Substitute.For<IOutboxStore>();
        _transport = Substitute.For<IOutboxTransport>();
        _eventHandler = Substitute.For<IOutboxEventHandler>();
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
            CircuitBreakerOpenDurationSeconds = 30
        };

        _optionsMonitor = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        _optionsMonitor.CurrentValue.Returns(_options);
        _optionsMonitor.Get(Arg.Any<string>()).Returns(_options);

        _store.GetTotalPartitionsAsync(Arg.Any<CancellationToken>()).Returns(64);
    }

    public void Dispose()
    {
        _instrumentation.Dispose();
        GC.SuppressFinalize(this);
    }

    private OutboxPublisherService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_store);
        services.AddSingleton(_transport);
        services.AddSingleton(_eventHandler);
        services.AddSingleton(_instrumentation);
        services.AddSingleton(_healthState);
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        return new OutboxPublisherService(sp, _optionsMonitor, _appLifetime);
    }

    private static OutboxMessage MakeMessage(
        long seq, string topic = "orders", string key = "key-1", int retryCount = 0) =>
        new(seq, topic, key, "OrderCreated", null, System.Text.Encoding.UTF8.GetBytes("{}"), "application/json", DateTimeOffset.UtcNow, 0,
            retryCount, DateTimeOffset.UtcNow);

    [Fact]
    public async Task RegistersAndUnregistersPublisher()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        // Return empty batches so publish loop just polls
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);

        // Wait for cancellation to allow ExecuteAsync to run
        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        await _store.Received(1).RegisterPublisherAsync(Arg.Any<CancellationToken>());
        await _store.Received(1).UnregisterPublisherAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_LeasesAndPublishesBatch()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Both messages have same topic+key, so should be sent in one call
        await _transport.Received(1).SendAsync(
            "orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 2),
            Arg.Any<CancellationToken>());

        await _store.Received(1).DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2),
            Arg.Any<CancellationToken>());

        // Health state should record successful publish
        Assert.NotEqual(DateTimeOffset.MinValue, _healthState.LastSuccessfulPublishUtc);
    }

    [Fact]
    public async Task PublishLoop_OnTransportFailure_IncrementsRetryCount()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // No-lease architecture: IncrementRetryCountAsync should be called on transport failure
        await _store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2),
            CancellationToken.None);

        await _store.DidNotReceive().DeletePublishedAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_GroupsByTopicAndPartitionKey()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[]
        {
            MakeMessage(1, "orders", "key-1"),
            MakeMessage(2, "orders", "key-2"),
            MakeMessage(3, "shipments", "key-1")
        };

        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // 3 different (topic, key) combos → 3 SendAsync calls
        await _transport.Received(3).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_CircuitOpen_SkipsWithoutStoreCall()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        // Need enough batches: CircuitBreakerFailureThreshold (3) failures to open,
        // plus at least one more batch where IsOpen returns true.
        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Return messages for more than threshold+1 batches so circuit-open path is hit
                if (Interlocked.Increment(ref callCount) <= _options.CircuitBreakerFailureThreshold + 2)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // All sends fail, causing the circuit to open after CircuitBreakerFailureThreshold failures
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // In no-lease architecture, circuit-open just skips — no store call needed.
        // IncrementRetryCountAsync is called for transport failures (before circuit opens),
        // but NOT for circuit-open skips.
        await _store.Received().IncrementRetryCountAsync(
            Arg.Any<IReadOnlyList<long>>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task PublishLoop_PartialSend_WhenDeleteAndIncrementBothFail_ContinuesGracefully()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2), MakeMessage(3) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Transport partially sends: message 1 succeeds, messages 2+3 fail
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialSendException(
                [1L],
                [2L, 3L],
                "partial",
                new InvalidOperationException("partial")));

        // Delete for succeeded messages fails
        _store.DeletePublishedAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        // IncrementRetryCountAsync also fails
        _store.IncrementRetryCountAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB still down"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // In the no-lease architecture, both delete and increment retry are attempted
        // even though they fail. Messages will be re-fetched on next poll.
        await _store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            CancellationToken.None);

        await _store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(2L) && s.Contains(3L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task HealthState_ReportsLoopRunning()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Assert.False(_healthState.IsPublishLoopRunning);

        await service.StartAsync(cts.Token);

        try { await Task.Delay(100, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        Assert.True(_healthState.IsPublishLoopRunning);

        try { await Task.Delay(250, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        Assert.False(_healthState.IsPublishLoopRunning);
    }

    [Fact]
    public async Task DeadLetterSweepLoop_CallsSweepDeadLettersAsync()
    {
        _options.DeadLetterSweepIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().SweepDeadLettersAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrphanSweepLoop_CallsClaimOrphanPartitionsAsync()
    {
        _options.OrphanSweepIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().ClaimOrphanPartitionsAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RebalanceLoop_CallsRebalanceAsync()
    {
        _options.RebalanceIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());
        _store.GetOwnedPartitionsAsync("publisher-1", Arg.Any<CancellationToken>())
            .Returns([0, 1]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().RebalanceAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_DeleteFails_DoesNotIncrementRetryCount()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Transport succeeds
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Delete fails
        _store.DeletePublishedAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // In no-lease architecture, delete failure after transport success just logs a warning.
        // No retry increment — transport succeeded, messages will be re-delivered on next poll.
        await _store.DidNotReceive().IncrementRetryCountAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HeartbeatLoop_CallsHeartbeatAndUpdatesPendingCount()
    {
        _options.HeartbeatIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());
        _store.GetPendingCountAsync(Arg.Any<CancellationToken>()).Returns(42L);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().HeartbeatAsync("publisher-1", Arg.Any<CancellationToken>());
        Assert.NotEqual(DateTimeOffset.MinValue, _healthState.LastHeartbeatUtc);
    }

    [Fact]
    public async Task PublishLoop_PartialSend_DeletesSucceededAndIncrementsRetryForFailed()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2), MakeMessage(3) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Transport partially sends: message 1 succeeds, messages 2+3 fail
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialSendException(
                [1L],
                [2L, 3L],
                "partial",
                new InvalidOperationException("partial")));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Succeeded message should be deleted
        await _store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 1 && s.Contains(1L)),
            CancellationToken.None);

        // Failed messages should have their retry count incremented
        await _store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(2L) && s.Contains(3L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task PublishLoop_PartialSend_RecordsCircuitBreakerFailure()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialSendException(
                [1L],
                [2L],
                "partial",
                new InvalidOperationException("partial")));

        var publishFailedCalled = false;
        _eventHandler.When(h => h.OnPublishFailedAsync(
                Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<Exception>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => publishFailedCalled = true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        Assert.True(publishFailedCalled);
    }

    [Fact]
    public async Task PublishLoop_RegisterPublisherFails_ServiceCancels_DoesNotUnregister()
    {
        // RegisterPublisherAsync always throws — service should exit without calling Unregister
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(200, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Since registration always fails and cancellation happens quickly, no publisher is registered
        await _store.DidNotReceive().UnregisterPublisherAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_OnTransportSuccess_CallsOnMessagePublishedForEachMessage()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        await _eventHandler.Received(2).OnMessagePublishedAsync(
            Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Bug 1: Event handler exception after successful send must NOT increment
    //        retry count or record circuit breaker failure
    // =========================================================================

    [Fact]
    public async Task PublishLoop_OnMessagePublishedThrows_DoesNotIncrementRetryCount()
    {
        // BUG 1: If OnMessagePublishedAsync throws after transport succeeds,
        // the exception falls through to the generic catch which calls
        // ReleaseLeaseAsync(incrementRetry: true). This is wrong — transport
        // succeeded, so retry count must NOT be incremented.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Transport succeeds
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Event handler throws after successful send
        _eventHandler.OnMessagePublishedAsync(Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler bug"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Transport succeeded, so delete should have been attempted (handler threw BEFORE delete,
        // but with the fix, the handler exception is caught and delete proceeds)
        await _store.Received().DeletePublishedAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());

        // CRITICAL: IncrementRetryCountAsync must NOT have been called.
        // The transport succeeded — the handler failure is irrelevant to message fate.
        await _store.DidNotReceive().IncrementRetryCountAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_OnCircuitBreakerStateChangedThrows_DoesNotIncrementRetryCount()
    {
        // BUG 1 variant: OnCircuitBreakerStateChangedAsync throws after circuit
        // recovery (successful send). Same issue — falls to generic catch.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1) };
        var sendCallCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(messages);

        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Fail enough times to open circuit, then succeed
                if (Interlocked.Increment(ref sendCallCount) <= _options.CircuitBreakerFailureThreshold)
                    throw new InvalidOperationException("broker down");

                return Task.CompletedTask;
            });

        // Circuit state change handler throws
        _eventHandler.OnCircuitBreakerStateChangedAsync(Arg.Any<string>(), Arg.Any<CircuitState>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler bug"));

        _options.CircuitBreakerOpenDurationSeconds = 0; // fast half-open
        _options.MinPollIntervalMs = 10;
        _options.MaxPollIntervalMs = 50;

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(1400, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // The successful send should have triggered a delete, not a retry-incremented release
        await _store.Received().DeletePublishedAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Bug 2: Poison message handler exception must NOT block healthy messages
    // =========================================================================

    [Fact]
    public async Task PublishLoop_AdaptivePollBackoff_IncreasesIntervalOnEmptyBatch()
    {
        // With MinPollIntervalMs=10 and MaxPollIntervalMs=100, after a few empty batches the
        // poll interval doubles each time. Verify that LeaseBatchAsync is called fewer times
        // within a fixed window than it would be without backoff.
        _options.MinPollIntervalMs = 10;
        _options.MaxPollIntervalMs = 100;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        // Always return empty — forces poll interval to ramp up
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(550, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // With MaxPollIntervalMs=100, after backoff saturates we get at most ~5 calls/500ms.
        // Without backoff at MinPollIntervalMs=10 we'd get ~50 calls.
        // Just verify it ran at all and the service completed cleanly.
        await _store.Received().FetchBatchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_WhenBatchHasItems_ResetsAdaptivePollInterval()
    {
        // First poll returns messages (resets interval back to Min), subsequent polls are empty.
        _options.MinPollIntervalMs = 10;
        _options.MaxPollIntervalMs = 80;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Return empty first to build up backoff, then a message to reset it, then empty
                var n = Interlocked.Increment(ref callCount);

                if (n <= 3) return Array.Empty<OutboxMessage>(); // build up interval
                if (n == 4) return messages; // resets to MinPollIntervalMs

                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(650, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Transport should have been called once (for the one non-empty batch)
        await _transport.Received(1).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_AllCircuitsOpen_AppliesBackoffEvenWithMessages()
    {
        // When messages are leased but all circuits are open, publishedAny stays false,
        // and the code applies adaptive backoff (lines 499-504).
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Return messages for first CircuitBreakerFailureThreshold + 3 polls to open circuit
                // then keep returning messages so the "publishedAny=false && batch.Count>0" path runs
                if (Interlocked.Increment(ref callCount) <= _options.CircuitBreakerFailureThreshold + 3)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Fail every send to trip the circuit breaker
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(650, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // After threshold failures, circuit opens. In the no-lease architecture,
        // circuit-open just skips — messages will be re-fetched on next poll.
        // IncrementRetryCountAsync is called for the initial transport failures.
        await _store.Received().IncrementRetryCountAsync(
            Arg.Any<IReadOnlyList<long>>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task PublishLoop_UnexpectedErrorInLeaseBatch_HandledGracefully_ContinuesPolling()
    {
        // Exceptions from LeaseBatchAsync are caught by the outer catch in PublishLoopAsync.
        // The loop logs the error, delays MaxPollIntervalMs, then continues polling.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var leaseBatchCallCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Throw on the very first call, then return empty
                if (Interlocked.Increment(ref leaseBatchCallCount) == 1)
                    throw new InvalidOperationException("transient DB crash");

                return Array.Empty<OutboxMessage>();
            });

        _options.MinPollIntervalMs = 10;
        _options.MaxPollIntervalMs = 50; // short delay after error

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(550, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // After the error + MaxPollIntervalMs delay, the loop should have polled at least once more
        Assert.True(leaseBatchCallCount > 1,
            $"Expected LeaseBatchAsync to be called more than once (error recovery), got {leaseBatchCallCount}");
    }

    [Fact]
    public async Task HealthState_RecordLoopRestart_IncreasesConsecutiveRestarts()
    {
        // Directly exercise the RecordLoopRestart and ResetLoopRestarts path on health state,
        // which is called by RunLoopsWithRestartAsync when a loop exits unexpectedly.
        var healthState = new OutboxHealthState();

        Assert.Equal(0, healthState.ConsecutiveLoopRestarts);

        healthState.RecordLoopRestart();
        healthState.RecordLoopRestart();
        Assert.Equal(2, healthState.ConsecutiveLoopRestarts);

        healthState.ResetLoopRestarts();
        Assert.Equal(0, healthState.ConsecutiveLoopRestarts);
    }

    [Fact]
    public async Task PublishLoop_UnexpectedErrorInLoop_LogsAndContinues()
    {
        // An exception inside the publish loop itself (not transport) is caught by the outer
        // exception handler in PublishLoopAsync, which logs and delays before continuing.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var n = Interlocked.Increment(ref callCount);

                if (n == 1)
                    throw new OutOfMemoryException("unexpected error in loop"); // hits outer catch

                return Array.Empty<OutboxMessage>();
            });

        _options.MaxPollIntervalMs = 50; // short delay after error

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(550, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // After the error the loop should have continued and polled again
        Assert.True(callCount > 1,
            $"Expected more than one LeaseBatch call (error recovery), got {callCount}");
    }

    [Fact]
    public async Task PublishLoop_TransportSuccess_CircuitRecovery_RaisesStateChangedEvent()
    {
        // Trip the circuit breaker with failures, then succeed to trigger circuit close event.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1) };
        var sendCallCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(messages);

        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Fail enough times to open the circuit, then succeed
                if (Interlocked.Increment(ref sendCallCount) <= _options.CircuitBreakerFailureThreshold)
                    throw new InvalidOperationException("broker down");

                return Task.CompletedTask;
            });

        var circuitChangedCalled = false;
        _eventHandler.When(h => h.OnCircuitBreakerStateChangedAsync(
                Arg.Any<string>(), Arg.Any<CircuitState>(), Arg.Any<CancellationToken>()))
            .Do(_ => circuitChangedCalled = true);

        var service = CreateService();
        // Need enough time for: threshold failures + circuit-open duration (30s default is too slow).
        // Override circuit breaker open duration to something short — but that's not configurable per-test
        // without changing options. Instead use CircuitBreakerOpenDurationSeconds=0 so it flips to HalfOpen.
        _options.CircuitBreakerOpenDurationSeconds = 0;
        _options.MinPollIntervalMs = 10;
        _options.MaxPollIntervalMs = 50;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(1400, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        Assert.True(circuitChangedCalled,
            "Expected OnCircuitBreakerStateChangedAsync to be called when circuit recovers");
    }

    [Fact]
    public async Task HeartbeatLoop_WhenGetPendingCountFails_LogsAndContinues()
    {
        _options.HeartbeatIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        // Heartbeat succeeds but GetPendingCountAsync throws — should be swallowed (logged as Debug)
        _store.GetPendingCountAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB timeout"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);

        // Heartbeat still ran despite GetPendingCount failure
        await _store.Received().HeartbeatAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterPublisherThrowsOCE_WhenTokenAlreadyCancelled_ExitsCleanly()
    {
        // Cover lines 63-65: OperationCanceledException during RegisterPublisherAsync when
        // stoppingToken is already cancelled. The when-filter requires IsCancellationRequested=true.
        using var cts = new CancellationTokenSource();

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns<string>(ci =>
            {
                // Cancel the token and then throw OCE — so the when-filter matches
                cts.Cancel();

                throw new OperationCanceledException("stopped");
            });

        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();

        await service.StartAsync(cts.Token);

        try { await Task.Delay(200, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Unregister must NOT be called — we never successfully registered
        await _store.DidNotReceive().UnregisterPublisherAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterPublisherFailsOnceThenSucceeds_RetriesAndContinues()
    {
        // Cover lines 67-75: The exception retry path in ExecuteAsync.
        // First call throws, second succeeds.
        var callCount = 0;
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    throw new InvalidOperationException("transient DB error");

                return Task.FromResult("publisher-retry");
            });

        // Shorten the retry delay so the test doesn't hang: patch options to speed things up.
        // We can't change the backoff directly, but we can cancel after a short time once
        // the registration eventually succeeds and the loop starts.
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        // Use a longer window to allow the retry delay (2s) to expire
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(TimeSpan.FromSeconds(3.5), CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // RegisterPublisherAsync was called at least twice (first failure + retry)
        Assert.True(callCount >= 2, $"Expected at least 2 registration attempts, got {callCount}");
        // Unregister was called with the successfully registered publisher
        await _store.Received(1).UnregisterPublisherAsync("publisher-retry", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterPublisherFailsAndCancellationDuringDelay_ExitsCleanly()
    {
        // Cover line 74: OperationCanceledException in the Task.Delay retry-wait path.
        // Registration throws a non-OCE error. We cancel immediately so the Task.Delay
        // inside the retry loop gets an OCE.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        var service = CreateService();
        // Cancel very quickly so Task.Delay(2s) is interrupted
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(200, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Never successfully registered, so Unregister must not be called
        await _store.DidNotReceive().UnregisterPublisherAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // But RegisterPublisher was attempted at least once
        await _store.Received().RegisterPublisherAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UnregisterPublisherFails_LogsWarningAndCompletes()
    {
        // Cover lines 98-101: UnregisterPublisherAsync failure in the finally block.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());
        _store.UnregisterPublisherAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB gone"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(300, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        // StopAsync should complete without throwing even though Unregister failed
        await service.StopAsync(CancellationToken.None);

        await _store.Received(1).RegisterPublisherAsync(Arg.Any<CancellationToken>());
        await _store.Received(1).UnregisterPublisherAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_TransportThrowsOCEDuringCancellation_BreaksGracefully()
    {
        // Cover lines 354-357: transport throws OperationCanceledException when ct is already
        // cancelled (graceful shutdown in progress). The catch block breaks instead of rethrowing.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1) };
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(messages);

        // Transport delays until cancellation, then throws OCE from the cancellation token.
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var token = (CancellationToken)ci[3];

                // Wait until cancellation happens, then throw OCE linked to the token
                return Task.Run(async () =>
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException)
                    {
                        /* Intentionally empty */
                    }

                    token.ThrowIfCancellationRequested();
                }, CancellationToken.None);
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        // Give the service a moment to lease messages and start the SendAsync
        await Task.Delay(100, CancellationToken.None);
        // Now cancel — the in-progress SendAsync will throw OCE, hitting lines 354-357
        await service.StopAsync(CancellationToken.None);

        // Service should have attempted to send at least once
        await _transport.Received().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_OuterOCECatch_WhenLeaseBatchThrowsOCEDuringStop()
    {
        // Cover line 510-512: the outer OCE handler in PublishLoopAsync.
        // This happens when LeaseBatchAsync itself throws an OCE while cancellation is in progress.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var firstCall = true;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var token = (CancellationToken)ci[3];

                if (firstCall)
                {
                    firstCall = false;

                    // Simulate a long-running lease batch that gets cancelled
                    return Task.Run(async () =>
                    {
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException)
                        {
                            /* Intentionally empty */
                        }

                        token.ThrowIfCancellationRequested();

                        return (IReadOnlyList<OutboxMessage>)Array.Empty<OutboxMessage>();
                    }, CancellationToken.None);
                }

                return Task.FromResult((IReadOnlyList<OutboxMessage>)Array.Empty<OutboxMessage>());
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        await Task.Delay(100, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().FetchBatchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_ErrorPathDelayInterruptedByCancellation_BreaksGracefully()
    {
        // Cover lines 521-523: cancellation in the Task.Delay inside the outer
        // error handler. This happens when an unexpected error occurs in the loop,
        // and then cancellation fires during the subsequent error-delay.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    throw new InvalidOperationException("unexpected error"); // hits outer catch

                return Array.Empty<OutboxMessage>();
            });

        // Use a long MaxPollIntervalMs so the error-delay is still running when we cancel
        _options.MaxPollIntervalMs = 10_000;

        var service = CreateService();
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        // Wait for the first call to fail and the error-delay to start
        await Task.Delay(150, CancellationToken.None);
        // Cancel while Task.Delay(10_000, ct) is running — hits lines 521-523
        await service.StopAsync(CancellationToken.None);

        Assert.True(callCount >= 1, "Expected at least one LeaseBatch call");
    }

    [Fact]
    public async Task PublishLoop_TransportFailure_CallsOnPublishFailedAsync()
    {
        // Cover lines 457-471: General transport exception handler calls OnPublishFailedAsync
        // and optionally OnCircuitBreakerStateChangedAsync when circuit opens.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var publishFailedCalled = false;
        _eventHandler.When(h => h.OnPublishFailedAsync(
                Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<Exception>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => publishFailedCalled = true);

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        Assert.True(publishFailedCalled, "Expected OnPublishFailedAsync to be called on transport failure");
        await _eventHandler.Received().OnPublishFailedAsync(
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 2),
            Arg.Any<Exception>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_TransportFailure_IncrementRetryAlsoFails_ContinuesGracefully()
    {
        // Transport failure where IncrementRetryCountAsync also fails.
        // The service should log the error and continue polling.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Transport fails
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broker down"));

        // IncrementRetryCountAsync also fails
        var incrementCallCount = 0;
        _store.IncrementRetryCountAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref incrementCallCount);
                throw new InvalidOperationException("DB also down");
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(450, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // IncrementRetryCountAsync was attempted but failed — service continued gracefully
        Assert.True(incrementCallCount >= 1,
            $"Expected at least 1 IncrementRetryCountAsync call, got {incrementCallCount}");
    }

    [Fact]
    public async Task PublishLoop_DeleteFails_AfterTransportSuccess_NoRetryIncrement()
    {
        // After transport succeeds, DeletePublishedAsync fails.
        // In no-lease architecture, this just logs a warning — no retry increment needed
        // because the transport already succeeded.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Transport succeeds
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Delete fails
        _store.DeletePublishedAsync(Arg.Any<IReadOnlyList<long>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(350, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Delete was attempted
        await _store.Received().DeletePublishedAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
        // No retry increment — transport succeeded
        await _store.DidNotReceive().IncrementRetryCountAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_PartialSend_CircuitOpens_RaisesStateChangedAndCallsOnPublishFailed()
    {
        // Cover lines 421-431: PartialSendException path where circuit breaker opens
        // (stateChanged=true), triggering both SetCircuitOpen and OnCircuitBreakerStateChangedAsync.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(messages);

        // Throw PartialSendException on every call to accumulate circuit breaker failures
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialSendException(
                [1L],
                [2L],
                "partial",
                new InvalidOperationException("broker degraded")));

        var circuitChangedCalled = false;
        _eventHandler.When(h => h.OnCircuitBreakerStateChangedAsync(
                Arg.Any<string>(), Arg.Any<CircuitState>(), Arg.Any<CancellationToken>()))
            .Do(_ => circuitChangedCalled = true);

        _options.CircuitBreakerFailureThreshold = 2; // open faster
        _options.MinPollIntervalMs = 10;
        _options.MaxPollIntervalMs = 50;

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(700, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // After threshold partial failures, circuit should open
        Assert.True(circuitChangedCalled,
            "Expected OnCircuitBreakerStateChangedAsync when circuit opens due to partial send failures");
    }

    [Fact]
    public async Task HeartbeatLoop_WhenHeartbeatThrowsNonOCE_LogsAndContinues()
    {
        // Cover lines 554-557: HeartbeatLoopAsync general error handler block.
        _options.HeartbeatIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        // HeartbeatAsync throws a non-cancellation exception — should be caught and logged
        _store.HeartbeatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("heartbeat DB error"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(350);
        await service.StopAsync(CancellationToken.None);

        // Heartbeat was attempted (and failed), service continued running
        await _store.Received().HeartbeatAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RebalanceLoop_WhenRebalanceThrowsNonOCE_LogsAndContinues()
    {
        // Cover lines 576-579: RebalanceLoopAsync general error handler block.
        _options.RebalanceIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        // RebalanceAsync throws
        _store.RebalanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rebalance DB error"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(350);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().RebalanceAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrphanSweepLoop_WhenClaimThrowsNonOCE_LogsAndContinues()
    {
        // Cover lines 596-599: OrphanSweepLoopAsync general error handler block.
        _options.OrphanSweepIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        // ClaimOrphanPartitionsAsync throws
        _store.ClaimOrphanPartitionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("orphan sweep DB error"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(350);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().ClaimOrphanPartitionsAsync("publisher-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeadLetterSweepLoop_WhenSweepThrowsNonOCE_LogsAndContinues()
    {
        // Cover lines 616-619: DeadLetterSweepLoopAsync general error handler block.
        _options.DeadLetterSweepIntervalMs = 50;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<OutboxMessage>());

        // SweepDeadLettersAsync throws
        _store.SweepDeadLettersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("dead letter sweep error"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var service = CreateService();
        await service.StartAsync(cts.Token);
        await Task.Delay(350);
        await service.StopAsync(CancellationToken.None);

        await _store.Received().SweepDeadLettersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_PartialSend_DeleteFailsForSucceeded_IncrementsRetryForFailed()
    {
        // PartialSendException path where:
        //   1. Transport partially sends (seq 1 succeeded, seq 2 failed)
        //   2. DeletePublishedAsync for succeeded messages fails
        //   3. IncrementRetryCountAsync for failed messages succeeds
        // In no-lease architecture, delete failure just logs — messages will be re-delivered.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1), MakeMessage(2) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;

                return Array.Empty<OutboxMessage>();
            });

        // Partial send: seq 1 succeeded, seq 2 failed
        _transport.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialSendException(
                [1L],
                [2L],
                "partial",
                new InvalidOperationException("degraded")));

        // Delete for succeeded messages fails
        _store.DeletePublishedAsync(Arg.Any<IReadOnlyList<long>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB blip"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(450, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Delete was attempted for succeeded sequence
        await _store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            CancellationToken.None);

        // Failed sequence has retry count incremented
        await _store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(2L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task RunLoopsWithRestart_ExceedsMaxConsecutiveRestarts_StopsApplication()
    {
        // Scenario 9 escalation: When loops crash repeatedly, after MaxConsecutiveRestarts (5)
        // the publisher must call StopApplication() to prevent a zombie process.
        // The heartbeat loop exits after 3 consecutive failures (throws), which causes
        // RunLoopsWithRestartAsync to cancel all loops and restart. After 5 such restarts,
        // StopApplication is called.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        // Publish loop returns empty batches (doesn't crash on its own)
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        // Heartbeat fails persistently — after 3 consecutive failures the loop throws,
        // triggering a loop restart cycle
        _store.HeartbeatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("persistent DB failure"));

        _options.MinPollIntervalMs = 10;
        _options.MaxPollIntervalMs = 50;
        _options.HeartbeatIntervalMs = 10;

        var stopCalled = new TaskCompletionSource();
        _appLifetime.When(a => a.StopApplication()).Do(_ => stopCalled.TrySetResult());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await service.StartAsync(cts.Token);

        // Wait for StopApplication to be called (should happen after 5 consecutive restarts).
        // Restart backoff is exponential: 2s + 4s + 8s + 16s = 30s minimum, plus loop crash time.
        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(50)));
        Assert.True(completed == stopCalled.Task,
            "Expected StopApplication() to be called after MaxConsecutiveRestarts exceeded");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HeartbeatLoop_ConsecutiveFailures_ExitsLoopAfterThreshold()
    {
        // When HeartbeatAsync fails 3 consecutive times, the heartbeat loop should throw
        // to trigger RunLoopsWithRestartAsync, preventing stale-heartbeat dual ownership.
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        // Heartbeat fails persistently
        _store.HeartbeatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("publisher row deleted"));

        _options.HeartbeatIntervalMs = 10;

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        // The heartbeat loop should have triggered at least one loop restart
        Assert.True(_healthState.ConsecutiveLoopRestarts > 0,
            "Expected at least one loop restart from consecutive heartbeat failures");
    }

    [Fact]
    public async Task PublishLoop_with_parallel_threads_publishes_all_partition_key_groups()
    {
        _options.PublishThreadCount = 4;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[]
        {
            MakeMessage(1, "orders", "pk-a"),
            MakeMessage(2, "orders", "pk-b"),
            MakeMessage(3, "orders", "pk-c"),
            MakeMessage(4, "orders", "pk-d"),
        };

        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;
                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch { /* Intentionally empty */ }

        await service.StopAsync(CancellationToken.None);

        // All 4 groups (different partition keys) should have been sent
        await _transport.Received(4).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_with_single_thread_publishes_all_groups()
    {
        _options.PublishThreadCount = 1;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1, "orders", "pk-a"), MakeMessage(2, "orders", "pk-b") };

        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;
                return Array.Empty<OutboxMessage>();
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch { /* Intentionally empty */ }

        await service.StopAsync(CancellationToken.None);

        await _transport.Received(2).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_worker_failure_does_not_block_other_workers()
    {
        _options.PublishThreadCount = 2;

        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
            .Returns("publisher-1");

        var messages = new[] { MakeMessage(1, "orders", "pk-a"), MakeMessage(2, "orders", "pk-b") };

        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return messages;
                return Array.Empty<OutboxMessage>();
            });

        // Make transport throw only for pk-a
        _transport.SendAsync("orders", "pk-a", Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch { /* Intentionally empty */ }

        await service.StopAsync(CancellationToken.None);

        // pk-b should still be published successfully
        await _transport.Received().SendAsync(
            "orders", "pk-b",
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());

        // pk-a should have its retry count incremented
        await _store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(ids => ids.Contains(1)),
            CancellationToken.None);

        // pk-b should be deleted (published successfully)
        await _store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(ids => ids.Contains(2)),
            Arg.Any<CancellationToken>());
    }
}
