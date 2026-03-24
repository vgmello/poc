// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Engine;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.Core.Tests;

public sealed class OutboxMessageInterceptorOrchestrationTests : IDisposable
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _optionsMonitor;
    private readonly OutboxInstrumentation _instrumentation;
    private readonly OutboxHealthState _healthState;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly OutboxPublisherOptions _options;

    public OutboxMessageInterceptorOrchestrationTests()
    {
        _store = Substitute.For<IOutboxStore>();
        _transport = Substitute.For<IOutboxTransport>();
        _eventHandler = Substitute.For<IOutboxEventHandler>();
        _instrumentation = new OutboxInstrumentation(new TestMeterFactory());
        _healthState = new OutboxHealthState();
        _appLifetime = Substitute.For<IHostApplicationLifetime>();

        _options = new OutboxPublisherOptions
        {
            BatchSize = 10, MaxRetryCount = 5,
            MinPollIntervalMs = 10, MaxPollIntervalMs = 100,
            HeartbeatIntervalMs = 100_000, RebalanceIntervalMs = 100_000,
            OrphanSweepIntervalMs = 100_000, DeadLetterSweepIntervalMs = 100_000,
            CircuitBreakerFailureThreshold = 3, CircuitBreakerOpenDurationSeconds = 30
        };

        _optionsMonitor = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        _optionsMonitor.CurrentValue.Returns(_options);
    }

    public void Dispose()
    {
        _instrumentation.Dispose();
        GC.SuppressFinalize(this);
    }

    private OutboxPublisherService CreateService(
        IReadOnlyList<IOutboxMessageInterceptor>? interceptors = null) =>
        new(_store, _transport, _eventHandler, _optionsMonitor,
            NullLogger<OutboxPublisherService>.Instance,
            _instrumentation, _healthState, _appLifetime,
            interceptors ?? Array.Empty<IOutboxMessageInterceptor>());

    private static OutboxMessage MakeMessage(long seq, string topic = "orders", string key = "key-1") =>
        new(seq, topic, key, "OrderCreated", null, Encoding.UTF8.GetBytes("{}"),
            "application/json", DateTimeOffset.UtcNow, 0, 0, DateTimeOffset.UtcNow);

    [Fact]
    public async Task NoInterceptors_MessagePassedUnchanged()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? messages : Array.Empty<OutboxMessage>());

        var service = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        await _transport.Received().SendAsync("orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 1 && m[0].SequenceNumber == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_TransformsPayload_TransportReceivesTransformedMessage()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? messages : Array.Empty<OutboxMessage>());

        var interceptor = Substitute.For<IOutboxMessageInterceptor>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<OutboxMessageContext>().Payload = Encoding.UTF8.GetBytes("transformed"));

        var service = CreateService(new[] { interceptor });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        await _transport.Received().SendAsync("orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m =>
                m.Count == 1 && Encoding.UTF8.GetString(m[0].Payload) == "transformed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_AppliesToReturnsFalse_MessagePassedUnchanged()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? messages : Array.Empty<OutboxMessage>());

        var interceptor = Substitute.For<IOutboxMessageInterceptor>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(false);

        var service = CreateService(new[] { interceptor });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        await interceptor.DidNotReceive().InterceptAsync(
            Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>());

        await _transport.Received().SendAsync("orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m =>
                m.Count == 1 && Encoding.UTF8.GetString(m[0].Payload) == "{}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleInterceptors_RunInOrder_SecondSeesMutationsFromFirst()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? messages : Array.Empty<OutboxMessage>());

        var order = new List<string>();

        var first = Substitute.For<IOutboxMessageInterceptor>();
        first.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        first.When(x => x.InterceptAsync(Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                order.Add("first");
                ci.Arg<OutboxMessageContext>().Payload = Encoding.UTF8.GetBytes("step1");
            });

        var second = Substitute.For<IOutboxMessageInterceptor>();
        second.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        second.When(x => x.InterceptAsync(Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                order.Add("second");
                var current = Encoding.UTF8.GetString(ci.Arg<OutboxMessageContext>().Payload);
                Assert.Equal("step1", current);
                ci.Arg<OutboxMessageContext>().Payload = Encoding.UTF8.GetBytes("step2");
            });

        var service = CreateService(new[] { first, second });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "first", "second" }, order);

        await _transport.Received().SendAsync("orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m =>
                m.Count == 1 && Encoding.UTF8.GetString(m[0].Payload) == "step2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_Throws_GroupReleasedWithRetryIncrement()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? messages : Array.Empty<OutboxMessage>());

        var interceptor = Substitute.For<IOutboxMessageInterceptor>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("interceptor boom"));

        var service = CreateService(new[] { interceptor });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        // Interceptor failure is treated as transport failure — retry count incremented.
        await _store.Received().ReleaseLeaseAsync("p1",
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            true, CancellationToken.None);

        // Transport should NOT have been called.
        await _transport.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EventHandler_ReceivesOriginalMessage_NotIntercepted()
    {
        _store.RegisterProducerAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var originalPayload = Encoding.UTF8.GetBytes("original");
        var messages = new[]
        {
            new OutboxMessage(1, "orders", "key-1", "OrderCreated", null,
                originalPayload, "application/json",
                DateTimeOffset.UtcNow, 0, 0, DateTimeOffset.UtcNow)
        };
        var callCount = 0;
        _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? messages : Array.Empty<OutboxMessage>());

        var interceptor = Substitute.For<IOutboxMessageInterceptor>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(ci => ci.Arg<OutboxMessageContext>().Payload = Encoding.UTF8.GetBytes("transformed"));

        var service = CreateService(new[] { interceptor });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch
        {
            /* Intentionally empty */
        }

        await service.StopAsync(CancellationToken.None);

        await _eventHandler.Received().OnMessagePublishedAsync(
            Arg.Is<OutboxMessage>(m => ReferenceEquals(m.Payload, originalPayload)),
            Arg.Any<CancellationToken>());
    }
}
