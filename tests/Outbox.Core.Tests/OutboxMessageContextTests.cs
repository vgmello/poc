// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests;

public class OutboxMessageContextTests
{
    private static OutboxMessage MakeMessage(
        long seq = 1,
        string topic = "orders",
        string key = "key-1",
        string eventType = "OrderCreated",
        Dictionary<string, string>? headers = null,
        string payloadContentType = "application/json") =>
        new(seq, topic, key, eventType, headers, Encoding.UTF8.GetBytes("{}"),
            payloadContentType, DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow);

    [Fact]
    public void Constructor_CopiesAllFields()
    {
        var headers = new Dictionary<string, string> { { "key", "val" } };
        var msg = MakeMessage(seq: 42, topic: "t", key: "k", eventType: "E",
            headers: headers, payloadContentType: "application/octet-stream");

        var ctx = new OutboxMessageContext(msg);

        Assert.Equal(42, ctx.SequenceNumber);
        Assert.Equal("t", ctx.TopicName);
        Assert.Equal("k", ctx.PartitionKey);
        Assert.Equal("E", ctx.EventType);
        Assert.Equal(msg.EventDateTimeUtc, ctx.EventDateTimeUtc);
        Assert.Equal(msg.EventOrdinal, ctx.EventOrdinal);
        Assert.Equal(msg.CreatedAtUtc, ctx.CreatedAtUtc);
        Assert.Same(msg.Payload, ctx.Payload);
        Assert.Equal(headers, ctx.Headers);
        Assert.NotSame(headers, ctx.Headers); // Defensive clone prevents mutation leaking to original
        Assert.Equal("application/octet-stream", ctx.PayloadContentType);
    }

    [Fact]
    public void ToOutboxMessage_PreservesReadOnlyFields_CarriesMutatedFields()
    {
        var original = MakeMessage(seq: 10, topic: "orders", key: "pk");
        var ctx = new OutboxMessageContext(original);

        var newPayload = Encoding.UTF8.GetBytes("encrypted");
        var newHeaders = new Dictionary<string, string> { { "new-key", "new-val" } };
        ctx.Payload = newPayload;
        ctx.Headers = newHeaders;
        ctx.PayloadContentType = "application/octet-stream";

        var result = ctx.ToOutboxMessage();

        Assert.Equal(10, result.SequenceNumber);
        Assert.Equal("orders", result.TopicName);
        Assert.Equal("pk", result.PartitionKey);
        Assert.Equal(original.EventType, result.EventType);
        Assert.Equal(original.EventDateTimeUtc, result.EventDateTimeUtc);
        Assert.Equal(original.EventOrdinal, result.EventOrdinal);
        Assert.Equal(original.CreatedAtUtc, result.CreatedAtUtc);
        Assert.Same(newPayload, result.Payload);
        Assert.Same(newHeaders, result.Headers);
        Assert.Equal("application/octet-stream", result.PayloadContentType);
    }

    [Fact]
    public void Payload_SetToNull_ThrowsArgumentNullException()
    {
        var ctx = new OutboxMessageContext(MakeMessage());

        Assert.Throws<ArgumentNullException>(() => ctx.Payload = null!);
    }

    [Fact]
    public void ToOutboxMessage_NoMutations_ReturnsEquivalentRecord()
    {
        var original = MakeMessage();
        var ctx = new OutboxMessageContext(original);

        var result = ctx.ToOutboxMessage();

        Assert.Equal(original.SequenceNumber, result.SequenceNumber);
        Assert.Same(original.Payload, result.Payload);
        Assert.Equal(original.Headers, result.Headers);
    }
}
