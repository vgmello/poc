// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests;

public sealed class TransportMessageContextTests
{
    private sealed record FakeEnvelope(string Body);

    private static OutboxMessage MakeMessage(long seq = 1) =>
        new(seq, "orders", "key-1", "OrderCreated", null,
            System.Text.Encoding.UTF8.GetBytes("{}"), "application/json",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void Constructor_SetsOriginalMessageAndEnvelope()
    {
        var source = MakeMessage();
        var envelope = new FakeEnvelope("test");

        var ctx = new TransportMessageContext<FakeEnvelope>(source, envelope);

        Assert.Same(source, ctx.OriginalMessage);
        Assert.Same(envelope, ctx.Envelope);
    }

    [Fact]
    public void Envelope_IsMutable()
    {
        var source = MakeMessage();
        var original = new FakeEnvelope("original");
        var replacement = new FakeEnvelope("replaced");

        var ctx = new TransportMessageContext<FakeEnvelope>(source, original);
        ctx.Envelope = replacement;

        Assert.Same(replacement, ctx.Envelope);
    }

    [Fact]
    public void OriginalMessage_ReflectsOriginalOutboxMessage()
    {
        var source = MakeMessage(42);
        var ctx = new TransportMessageContext<string>(source, "envelope");

        Assert.Equal(42L, ctx.OriginalMessage.SequenceNumber);
        Assert.Equal("orders", ctx.OriginalMessage.TopicName);
        Assert.Equal("key-1", ctx.OriginalMessage.PartitionKey);
    }

    [Fact]
    public void Constructor_NullOriginalMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TransportMessageContext<string>(null!, "envelope"));
    }

    [Fact]
    public void Constructor_NullEnvelope_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TransportMessageContext<string>(MakeMessage(), null!));
    }
}
