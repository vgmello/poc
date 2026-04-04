// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Engine;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Tests;

internal sealed class TestOutboxServiceFactory : IDisposable
{
    public IOutboxStore Store { get; }
    public IOutboxTransport Transport { get; }
    public IOutboxEventHandler EventHandler { get; }
    public IOptionsMonitor<OutboxPublisherOptions> OptionsMonitor { get; }
    public OutboxInstrumentation Instrumentation { get; }
    public OutboxHealthState HealthState { get; }
    public IHostApplicationLifetime AppLifetime { get; }
    public OutboxPublisherOptions Options { get; }

    public TestOutboxServiceFactory()
    {
        Store = Substitute.For<IOutboxStore>();
        Transport = Substitute.For<IOutboxTransport>();
        EventHandler = Substitute.For<IOutboxEventHandler>();
        Instrumentation = new OutboxInstrumentation(new TestMeterFactory());
        HealthState = new OutboxHealthState();
        AppLifetime = Substitute.For<IHostApplicationLifetime>();

        Options = new OutboxPublisherOptions
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

        OptionsMonitor = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        OptionsMonitor.CurrentValue.Returns(Options);
        OptionsMonitor.Get(Arg.Any<string>()).Returns(Options);

        Store.GetTotalPartitionsAsync(Arg.Any<CancellationToken>()).Returns(64);
    }

    public OutboxPublisherService CreateService(
        IReadOnlyList<IOutboxMessageInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Store);
        services.AddSingleton(Transport);
        services.AddSingleton(EventHandler);
        services.AddSingleton(Instrumentation);
        services.AddSingleton(HealthState);
        services.AddLogging();

        if (interceptors is not null)
        {
            foreach (var interceptor in interceptors)
                services.AddSingleton(interceptor);
        }

        var sp = services.BuildServiceProvider();
        return new OutboxPublisherService(sp, OptionsMonitor, AppLifetime);
    }

    /// <summary>
    /// Runs the service for a short duration and stops it. Standard pattern for behavioral tests.
    /// </summary>
    public static async Task RunServiceAsync(
        OutboxPublisherService service, int runMs = 300, int waitMs = 350)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(runMs));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(waitMs, CancellationToken.None); }
        catch { /* Intentionally empty */ }

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Registers publisher and sets up FetchBatch to return messages once, then empty.
    /// </summary>
    public void SetupSingleBatch(string publisherId, OutboxMessage[] messages)
    {
        Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns(publisherId);

        var callCount = 0;
        Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                Interlocked.Increment(ref callCount) == 1
                    ? messages
                    : Array.Empty<OutboxMessage>());
    }

    public static OutboxMessage MakeMessage(
        long seq, string topic = "orders", string key = "key-1",
        int retryCount = 0, DateTimeOffset? eventTime = null, int eventOrdinal = 0) =>
        new(seq, topic, key, "OrderCreated", null,
            System.Text.Encoding.UTF8.GetBytes("{}"), "application/json",
            eventTime ?? DateTimeOffset.UtcNow, eventOrdinal, retryCount, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Instrumentation.Dispose();
        GC.SuppressFinalize(this);
    }
}
