// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class CircuitBreakerContractTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task CircuitOpens_AfterThresholdConsecutiveFailures()
    {
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");

        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        var callCount = 0;
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                Interlocked.Increment(ref callCount) <= _f.Options.CircuitBreakerFailureThreshold + 2
                    ? messages
                    : Array.Empty<OutboxMessage>());

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        Assert.Contains("orders", _f.HealthState.GetOpenCircuits());
    }

    [Fact]
    public async Task OpenCircuit_SkipsMessages_WithoutTouchingThem()
    {
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");

        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        var callCount = 0;
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                Interlocked.Increment(ref callCount) <= _f.Options.CircuitBreakerFailureThreshold + 3
                    ? messages
                    : Array.Empty<OutboxMessage>());

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        // Transport should only have been called up to threshold times (before circuit opened).
        var sendCalls = _f.Transport.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "SendAsync");
        Assert.Equal(_f.Options.CircuitBreakerFailureThreshold, sendCalls);
    }

    [Fact]
    public async Task MessagesForOtherTopics_StillProcessed_WhenOneTopicCircuitOpen()
    {
        _f.Options.CircuitBreakerFailureThreshold = 1;
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");

        var batch1 = new[] { TestOutboxServiceFactory.MakeMessage(1, "orders", "key-1") };
        var batch2 = new[] { TestOutboxServiceFactory.MakeMessage(2, "shipments", "key-1") };

        var callCount = 0;
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1) return batch1;
                if (n == 2) return batch2;
                return Array.Empty<OutboxMessage>();
            });

        _f.Transport.SendAsync(
                "orders", Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("orders broker down"));

        _f.Transport.SendAsync(
                "shipments", Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Transport.Received().SendAsync(
            "shipments", "key-1",
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(2L)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HealthState_UpdatedBeforeEventHandler_OnCircuitStateChange()
    {
        _f.Options.CircuitBreakerFailureThreshold = 1;
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");

        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        var callCount = 0;
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                Interlocked.Increment(ref callCount) == 1
                    ? messages
                    : Array.Empty<OutboxMessage>());

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Broker down"));

        bool healthStateWasUpdatedBeforeHandler = false;
        _f.EventHandler.OnCircuitBreakerStateChangedAsync(
                Arg.Any<string>(), Arg.Any<CircuitState>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                healthStateWasUpdatedBeforeHandler = _f.HealthState.GetOpenCircuits().Contains("orders");
                return Task.CompletedTask;
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        Assert.True(healthStateWasUpdatedBeforeHandler);
    }

    [Fact]
    public async Task CircuitCloseOnSuccess_FiresEventHandler()
    {
        _f.Options.CircuitBreakerFailureThreshold = 1;
        _f.Options.CircuitBreakerOpenDurationSeconds = 0;
        _f.Options.MinPollIntervalMs = 10;
        _f.Options.MaxPollIntervalMs = 50;

        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");

        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(messages);

        var sendCallCount = 0;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref sendCallCount) == 1)
                    throw new InvalidOperationException("broker down");
                return Task.CompletedTask;
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 1000, 1100);

        await _f.EventHandler.Received().OnCircuitBreakerStateChangedAsync(
            "orders", CircuitState.Closed, Arg.Any<CancellationToken>());
    }
}
