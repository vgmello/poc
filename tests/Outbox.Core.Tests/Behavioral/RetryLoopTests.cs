// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class RetryLoopTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    private void SetAllTransient() =>
        _f.Transport.IsTransient(Arg.Any<Exception>()).Returns(true);

    [Fact]
    public async Task NonTransientFailures_ThenSuccess_Deletes_AndDoesNotDeadLetter()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        var callCount = 0;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref callCount);
                if (callCount < 3) throw new InvalidOperationException("poison");
                return Task.CompletedTask;
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            Arg.Any<CancellationToken>());
        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTransientFailures_DeadLetters_AfterMaxAttempts()
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
            .ThrowsAsync(new InvalidOperationException("permanent poison"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received(1).DeadLetterAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(1L) && s.Contains(2L)),
            _f.Options.MaxPublishAttempts,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _f.EventHandler.Received(1).OnPublishFailedAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<Exception>(),
            PublishFailureReason.RetriesExhausted,
            Arg.Any<CancellationToken>());

        await _f.EventHandler.Received(2).OnMessageDeadLetteredAsync(
            Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTransientFailures_DoNotRecord_CircuitBreakerFailures()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("poison"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.EventHandler.DidNotReceive().OnCircuitBreakerStateChangedAsync(
            Arg.Any<string>(), Arg.Any<CircuitState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailures_OpenCircuit_AndDoNotDeadLetter()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("broker down"));

        SetAllTransient();

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.EventHandler.Received().OnCircuitBreakerStateChangedAsync(
            Arg.Any<string>(), CircuitState.Open, Arg.Any<CancellationToken>());

        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await _f.EventHandler.Received().OnPublishFailedAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<Exception>(),
            PublishFailureReason.CircuitOpened,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailures_DoNotBurn_AttemptCounter()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("broker down"));
        SetAllTransient();

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartialSend_DeletesSucceeded_ThenRetriesFailedSubset()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2),
            TestOutboxServiceFactory.MakeMessage(3)
        };
        _f.SetupSingleBatch("p1", messages);

        var attempt = 0;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref attempt);
                if (attempt == 1)
                {
                    throw new PartialSendException(
                        succeededSequenceNumbers: new long[] { 1L },
                        failedSequenceNumbers: new long[] { 2L, 3L },
                        message: "partial",
                        innerException: new InvalidOperationException("inner"));
                }
                return Task.CompletedTask;
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 1 && s.Contains(1L)),
            Arg.Any<CancellationToken>());

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(2L) && s.Contains(3L)),
            Arg.Any<CancellationToken>());

        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartialSend_FailedSubset_NonTransientExhaustion_DeadLetters_OnlyFailedSubset()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2),
            TestOutboxServiceFactory.MakeMessage(3)
        };
        _f.SetupSingleBatch("p1", messages);

        var attempt = 0;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref attempt);
                if (attempt == 1)
                {
                    throw new PartialSendException(
                        new long[] { 1L }, new long[] { 2L, 3L }, "partial",
                        new InvalidOperationException("poison"));
                }
                throw new InvalidOperationException("poison");
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 1 && s.Contains(1L)),
            Arg.Any<CancellationToken>());

        await _f.Store.Received(1).DeadLetterAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(2L) && s.Contains(3L)),
            _f.Options.MaxPublishAttempts,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
