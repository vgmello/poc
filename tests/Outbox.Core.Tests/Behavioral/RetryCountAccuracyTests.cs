// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class RetryCountAccuracyTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task TransportException_IncrementsRetryCount_ForAllMessagesInGroup()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2)
        };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Kafka down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L) && s.Contains(2L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task PartialSend_IncrementsRetry_OnlyForFailedSequenceNumbers()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2),
            TestOutboxServiceFactory.MakeMessage(3)
        };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialSendException(
                [1L], [2L, 3L], "partial", new InvalidOperationException("partial")));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(2L) && s.Contains(3L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task PartialSend_DeletesOnly_SucceededSequenceNumbers()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2),
            TestOutboxServiceFactory.MakeMessage(3)
        };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PartialSendException(
                [1L], [2L, 3L], "partial", new InvalidOperationException("partial")));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 1 && s.Contains(1L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task CircuitOpenSkip_DoesNotIncrementRetryCount()
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

        // IncrementRetryCountAsync called only for the actual transport failures (threshold count),
        // NOT for the circuit-open skips afterward.
        var incrementCalls = _f.Store.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "IncrementRetryCountAsync");
        Assert.Equal(_f.Options.CircuitBreakerFailureThreshold, incrementCalls);
    }

    [Fact]
    public async Task DeleteFailure_DoesNotIncrementRetryCount()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Store.DeletePublishedAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.DidNotReceive().IncrementRetryCountAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EventHandlerException_AfterSuccessfulSend_DoesNotIncrementRetryCount()
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
    public async Task InterceptorException_IncrementsRetryCount()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        var interceptor = Substitute.For<IOutboxMessageInterceptor>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(
                Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("interceptor boom"));

        var service = _f.CreateService(new[] { interceptor });
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.Received().IncrementRetryCountAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            CancellationToken.None);

        await _f.Transport.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }
}
