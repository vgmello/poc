// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests;

public sealed class TransportMessageContextTests
{
    private static OutboxMessage MakeMessage(long seq = 1) =>
        new(seq, "orders", "key-1", "OrderCreated", null,
            System.Text.Encoding.UTF8.GetBytes("{}"), "application/json",
            DateTimeOffset.UtcNow, 0, 0, DateTimeOffset.UtcNow);

    [Fact]
    public void Constructor_SetsSourceMessageAndMessage()
    {
        var source = MakeMessage();
        var envelope = new { Topic = "orders", Body = "test" };

        var ctx = new TransportMessageContext<object>(source, envelope);

        Assert.Same(source, ctx.SourceMessage);
        Assert.Same(envelope, ctx.Message);
    }

    [Fact]
    public void Message_IsMutable()
    {
        var source = MakeMessage();
        var original = new { Body = "original" };
        var replacement = new { Body = "replaced" };

        var ctx = new TransportMessageContext<object>(source, original);
        ctx.Message = replacement;

        Assert.Same(replacement, ctx.Message);
    }

    [Fact]
    public void SourceMessage_ReflectsOriginalOutboxMessage()
    {
        var source = MakeMessage(42);
        var ctx = new TransportMessageContext<string>(source, "envelope");

        Assert.Equal(42L, ctx.SourceMessage.SequenceNumber);
        Assert.Equal("orders", ctx.SourceMessage.TopicName);
        Assert.Equal("key-1", ctx.SourceMessage.PartitionKey);
    }
}
