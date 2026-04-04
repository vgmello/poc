// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class LoopCoordinationContractTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task HeartbeatFailure_After3ConsecutiveAttempts_TriggersLoopRestart()
    {
        _f.Options.HeartbeatIntervalMs = 20;
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        _f.Store.HeartbeatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 1000, 1100);

        Assert.True(_f.HealthState.ConsecutiveLoopRestarts >= 1);
    }

    [Fact]
    public async Task AfterMaxConsecutiveRestarts_HostApplicationStopped()
    {
        _f.Options.HeartbeatIntervalMs = 10;
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        _f.Store.HeartbeatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 35_000, 35_100);

        _f.AppLifetime.Received().StopApplication();
    }

    [Fact]
    public async Task HealthState_RecordsLoopRestart()
    {
        _f.Options.HeartbeatIntervalMs = 10;
        _f.Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns("p1");
        _f.Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        var heartbeatCallCount = 0;
        _f.Store.HeartbeatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref heartbeatCallCount) <= 3)
                    throw new InvalidOperationException("DB down");
                return Task.CompletedTask;
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 5_000, 5_100);

        Assert.True(_f.HealthState.ConsecutiveLoopRestarts >= 1);
    }
}
