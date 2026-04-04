// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class AdaptivePollingTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task EmptyBatch_DoublesPollInterval_UpToMax()
    {
        _f.Options.MinPollIntervalMs = 10;
        _f.Options.MaxPollIntervalMs = 100;

        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        var fetchCalls = _f.Store.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "FetchBatchAsync");
        Assert.InRange(fetchCalls, 2, 15);
    }

    [Fact]
    public async Task NonEmptyBatch_ResetsPollInterval_ToMin()
    {
        _f.Options.MinPollIntervalMs = 10;
        _f.Options.MaxPollIntervalMs = 200;

        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");

        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        var callCount = 0;
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n <= 3) return Array.Empty<OutboxMessage>();
                if (n == 4) return messages;
                return Array.Empty<OutboxMessage>();
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 600, 650);

        await _f.Transport.Received(1).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllCircuitsOpen_WithMessagesInBatch_AppliesBackoff()
    {
        _f.Options.CircuitBreakerFailureThreshold = 1;
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");

        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        var callCount = 0;
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                Interlocked.Increment(ref callCount) <= 5
                    ? messages
                    : Array.Empty<OutboxMessage>());

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        var sendCalls = _f.Transport.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "SendAsync");
        Assert.Equal(1, sendCalls);
    }
}
