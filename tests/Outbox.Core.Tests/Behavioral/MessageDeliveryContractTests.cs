// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class MessageDeliveryContractTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task FetchedMessages_SentToTransport_WithCorrectTopicAndPartitionKey()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1, "orders", "key-1"),
            TestOutboxServiceFactory.MakeMessage(2, "orders", "key-1")
        };
        _f.SetupSingleBatch("p1", messages);

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Transport.Received(1).SendAsync(
            "orders", "key-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuccessfullySentMessages_DeletedFromStore()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2)
        };
        _f.SetupSingleBatch("p1", messages);

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received(1).DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L) && s.Contains(2L)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteFailure_AfterSuccessfulSend_DoesNotDeadLetterMessage()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Store.DeletePublishedAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleDifferentGroups_InSameBatch_AllProcessed()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1, "orders", "key-1"),
            TestOutboxServiceFactory.MakeMessage(2, "orders", "key-2"),
            TestOutboxServiceFactory.MakeMessage(3, "shipments", "key-1")
        };
        _f.SetupSingleBatch("p1", messages);

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        // 3 groups -> 3 SendAsync calls
        await _f.Transport.Received(3).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }
}
