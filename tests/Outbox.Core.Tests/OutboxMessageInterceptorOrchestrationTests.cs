// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            BatchSize = 10, MaxPublishAttempts = 5,
            MinPollIntervalMs = 10, MaxPollIntervalMs = 100,
            HeartbeatIntervalMs = 100_000, RebalanceIntervalMs = 100_000,
            OrphanSweepIntervalMs = 100_000,
            CircuitBreakerFailureThreshold = 3, CircuitBreakerOpenDurationSeconds = 30
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

    private OutboxPublisherService CreateService(
        IReadOnlyList<IOutboxMessageInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_store);
        services.AddSingleton(_transport);
        services.AddSingleton(_eventHandler);
        services.AddSingleton(_instrumentation);
        services.AddSingleton(_healthState);
        services.AddLogging();
        if (interceptors is not null)
        {
            foreach (var interceptor in interceptors)
                services.AddSingleton(interceptor);
        }
        var sp = services.BuildServiceProvider();
        return new OutboxPublisherService(sp, _optionsMonitor, _appLifetime);
    }

    private static OutboxMessage MakeMessage(long seq, string topic = "orders", string key = "key-1") =>
        new(seq, topic, key, "OrderCreated", null, Encoding.UTF8.GetBytes("{}"),
            "application/json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task NoInterceptors_MessagePassedUnchanged()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
    public async Task EventHandler_ReceivesOriginalMessage_NotIntercepted()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var originalPayload = Encoding.UTF8.GetBytes("original");
        var messages = new[]
        {
            new OutboxMessage(1, "orders", "key-1", "OrderCreated", null,
                originalPayload, "application/json",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task ThreeInterceptors_ChainCorrectly()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[] { MakeMessage(1) };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
                ci.Arg<OutboxMessageContext>().Payload = Encoding.UTF8.GetBytes("step2");
            });

        var third = Substitute.For<IOutboxMessageInterceptor>();
        third.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        third.When(x => x.InterceptAsync(Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                order.Add("third");
                var current = Encoding.UTF8.GetString(ci.Arg<OutboxMessageContext>().Payload);
                Assert.Equal("step2", current);
                ci.Arg<OutboxMessageContext>().Payload = Encoding.UTF8.GetBytes("step3");
            });

        var service = CreateService(new[] { first, second, third });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch { /* Intentionally empty */ }

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "first", "second", "third" }, order);

        await _transport.Received().SendAsync("orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m =>
                m.Count == 1 && Encoding.UTF8.GetString(m[0].Payload) == "step3"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_ModifiesHeaders_TransportReceivesModifiedHeaders()
    {
        _store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        var messages = new[]
        {
            new OutboxMessage(1, "orders", "key-1", "OrderCreated",
                new Dictionary<string, string> { ["original"] = "value" },
                Encoding.UTF8.GetBytes("{}"), "application/json",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };
        var callCount = 0;
        _store.FetchBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? messages : Array.Empty<OutboxMessage>());

        var interceptor = Substitute.For<IOutboxMessageInterceptor>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<OutboxMessageContext>();
                ctx.Headers!["added"] = "new-value";
            });

        var service = CreateService(new[] { interceptor });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(600, CancellationToken.None); }
        catch { /* Intentionally empty */ }

        await service.StopAsync(CancellationToken.None);

        await _transport.Received().SendAsync("orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m =>
                m.Count == 1 &&
                m[0].Headers != null &&
                m[0].Headers!.ContainsKey("added") &&
                m[0].Headers!["added"] == "new-value"),
            Arg.Any<CancellationToken>());
    }
}
