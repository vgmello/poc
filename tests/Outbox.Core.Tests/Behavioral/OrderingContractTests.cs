// Copyright (c) OrgName. All rights reserved.

using System.Text;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class OrderingContractTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task MessagesWithinGroup_SentOrdered_ByEventDateTimeUtc_ThenEventOrdinal_ThenSequenceNumber()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);

        // Deliberately insert out of order to verify sorting
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(3, eventTime: t2, eventOrdinal: 0),
            TestOutboxServiceFactory.MakeMessage(1, eventTime: t1, eventOrdinal: 0),
            TestOutboxServiceFactory.MakeMessage(2, eventTime: t1, eventOrdinal: 1),
        };
        _f.SetupSingleBatch("p1", messages);

        IReadOnlyList<OutboxMessage>? sentMessages = null;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => sentMessages = ci.ArgAt<IReadOnlyList<OutboxMessage>>(2));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        Assert.NotNull(sentMessages);
        Assert.Equal(3, sentMessages!.Count);
        // Expected order: seq 1 (t1, ord 0), seq 2 (t1, ord 1), seq 3 (t2, ord 0)
        Assert.Equal(1L, sentMessages[0].SequenceNumber);
        Assert.Equal(2L, sentMessages[1].SequenceNumber);
        Assert.Equal(3L, sentMessages[2].SequenceNumber);
    }

    [Fact]
    public async Task MultipleGroups_MaintainIndependentOrdering()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);

        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(2, "orders", "key-1", eventTime: t2),
            TestOutboxServiceFactory.MakeMessage(1, "orders", "key-1", eventTime: t1),
            TestOutboxServiceFactory.MakeMessage(4, "orders", "key-2", eventTime: t2),
            TestOutboxServiceFactory.MakeMessage(3, "orders", "key-2", eventTime: t1),
        };
        _f.SetupSingleBatch("p1", messages);

        var sentGroups = new List<(string Key, IReadOnlyList<OutboxMessage> Messages)>();
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                var key = ci.ArgAt<string>(1);
                var msgs = ci.ArgAt<IReadOnlyList<OutboxMessage>>(2);
                lock (sentGroups) { sentGroups.Add((key, msgs.ToList())); }
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        Assert.Equal(2, sentGroups.Count);

        var group1 = sentGroups.Single(g => g.Key == "key-1").Messages;
        Assert.Equal(1L, group1[0].SequenceNumber);
        Assert.Equal(2L, group1[1].SequenceNumber);

        var group2 = sentGroups.Single(g => g.Key == "key-2").Messages;
        Assert.Equal(3L, group2[0].SequenceNumber);
        Assert.Equal(4L, group2[1].SequenceNumber);
    }

    [Fact]
    public async Task Ordering_PreservedWhenInterceptorsTransformPayloads()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);

        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(2, eventTime: t2),
            TestOutboxServiceFactory.MakeMessage(1, eventTime: t1),
        };
        _f.SetupSingleBatch("p1", messages);

        var interceptor = Substitute.For<IOutboxMessageInterceptor>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.InterceptAsync(
                Arg.Any<OutboxMessageContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask)
            .AndDoes(ci => ci.Arg<OutboxMessageContext>().Payload = Encoding.UTF8.GetBytes("transformed"));

        IReadOnlyList<OutboxMessage>? sentMessages = null;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => sentMessages = ci.ArgAt<IReadOnlyList<OutboxMessage>>(2));

        var service = _f.CreateService(new[] { interceptor });
        await TestOutboxServiceFactory.RunServiceAsync(service);

        Assert.NotNull(sentMessages);
        Assert.Equal(2, sentMessages!.Count);
        Assert.Equal(1L, sentMessages[0].SequenceNumber);
        Assert.Equal(2L, sentMessages[1].SequenceNumber);
        Assert.Equal("transformed", Encoding.UTF8.GetString(sentMessages[0].Payload));
        Assert.Equal("transformed", Encoding.UTF8.GetString(sentMessages[1].Payload));
    }
}
