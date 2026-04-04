// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class EventHandlerIsolationTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task OnMessagePublished_Throws_DoesNotPreventDelete()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.EventHandler.OnMessagePublishedAsync(
                Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler bug"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnMessagePublished_Throws_DoesNotIncrementRetryCount()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.EventHandler.OnMessagePublishedAsync(
                Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler bug"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.DidNotReceive().IncrementRetryCountAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnPublishFailed_Throws_DoesNotAffectRetryCountIncrement()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Kafka down"));

        _f.EventHandler.OnPublishFailedAsync(
                Arg.Any<IReadOnlyList<OutboxMessage>>(),
                Arg.Any<Exception>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler bug"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task OnCircuitBreakerStateChanged_Throws_DoesNotAffectCircuitState()
    {
        _f.Options.CircuitBreakerFailureThreshold = 1;
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        _f.EventHandler.OnCircuitBreakerStateChangedAsync(
                Arg.Any<string>(), Arg.Any<CircuitState>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler bug"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        Assert.Contains("orders", _f.HealthState.GetOpenCircuits());
    }

    [Fact]
    public async Task OnRebalanceAsync_Throws_DoesNotCrashRebalanceLoop()
    {
        _f.Options.RebalanceIntervalMs = 30;
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());
        _f.Store.GetOwnedPartitionsAsync("p1", Arg.Any<CancellationToken>()).Returns(new[] { 0, 1 });

        _f.EventHandler.OnRebalanceAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler bug"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.Received().RebalanceAsync("p1", Arg.Any<CancellationToken>());
        Assert.Equal(0, _f.HealthState.ConsecutiveLoopRestarts);
    }
}
