// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class GracefulShutdownContractTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task UnregisterPublisher_CalledOnNormalShutdown()
    {
        _f.SetupSingleBatch("p1", Array.Empty<OutboxMessage>());

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received(1).UnregisterPublisherAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnregisterPublisher_UsesCancellationTokenNone()
    {
        _f.SetupSingleBatch("p1", Array.Empty<OutboxMessage>());

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received(1).UnregisterPublisherAsync("p1", CancellationToken.None);
    }

    [Fact]
    public async Task DeletePublished_InPartialSendPath_UsesCancellationTokenNone()
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
            .ThrowsAsync(new PartialSendException(
                [1L], [2L], "partial", new InvalidOperationException("partial")));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service, 500, 550);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            CancellationToken.None);
    }

    [Fact]
    public async Task UnregisterFailure_LogsWarning_DoesNotThrow()
    {
        _f.SetupSingleBatch("p1", Array.Empty<OutboxMessage>());

        _f.Store.UnregisterPublisherAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var service = _f.CreateService();

        // Should not throw even though unregister fails
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received(1).UnregisterPublisherAsync("p1", CancellationToken.None);
    }
}
