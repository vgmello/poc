// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.Core.Tests;

public class ModelTests
{
    [Fact]
    public void OutboxMessage_AllPropertiesAccessible()
    {
        var eventDate = DateTimeOffset.UtcNow;
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var headers = new Dictionary<string, string> { { "x-correlation-id", "abc" } };
        var payloadBytes = Encoding.UTF8.GetBytes("""{"id":1}""");

        var msg = new OutboxMessage(
            SequenceNumber: 42L,
            TopicName: "orders",
            PartitionKey: "order-123",
            EventType: "OrderCreated",
            Headers: headers,
            Payload: payloadBytes,
            PayloadContentType: "application/json",
            EventDateTimeUtc: eventDate,
            EventOrdinal: 3,
            CreatedAtUtc: createdAt);

        Assert.Equal(42L, msg.SequenceNumber);
        Assert.Equal("orders", msg.TopicName);
        Assert.Equal("order-123", msg.PartitionKey);
        Assert.Equal("OrderCreated", msg.EventType);
        Assert.Same(headers, msg.Headers);
        Assert.Equal(payloadBytes, msg.Payload);
        Assert.Equal("application/json", msg.PayloadContentType);
        Assert.Equal(eventDate, msg.EventDateTimeUtc);
        Assert.Equal(3, msg.EventOrdinal);
        Assert.Equal(createdAt, msg.CreatedAtUtc);
    }

    [Fact]
    public void OutboxMessage_NullHeaders_IsAllowed()
    {
        var msg = new OutboxMessage(1L, "topic", "key", "EventType", null, Encoding.UTF8.GetBytes("{}"), "application/json",
            DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow);
        Assert.Null(msg.Headers);
    }

    [Fact]
    public void OutboxMessage_RecordEquality_ByteArrayUsesReferenceEquality()
    {
        var ts = DateTimeOffset.UtcNow;
        var payload = Encoding.UTF8.GetBytes("{}");
        var msg1 = new OutboxMessage(1L, "topic", "key", "EventType", null, payload, "application/json", ts, 0, ts);
        var msg2 = new OutboxMessage(1L, "topic", "key", "EventType", null, payload, "application/json", ts, 0, ts);
        var msg3 = new OutboxMessage(1L, "topic", "key", "EventType", null, Encoding.UTF8.GetBytes("{}"), "application/json", ts, 0, ts);
        var msg4 = new OutboxMessage(2L, "topic", "key", "EventType", null, payload, "application/json", ts, 0, ts);

        Assert.Equal(msg1, msg2); // same byte[] reference
        Assert.NotEqual(msg1, msg3); // different byte[] reference
        Assert.NotEqual(msg1, msg4); // different SequenceNumber
    }

    [Fact]
    public void DeadLetteredMessage_AllPropertiesAccessible()
    {
        var eventDate = DateTimeOffset.UtcNow;
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var deadLetteredAt = DateTimeOffset.UtcNow;
        var headers = new Dictionary<string, string> { { "x-correlation-id", "abc" } };
        var payloadBytes = Encoding.UTF8.GetBytes("""{"id":1}""");

        var msg = new DeadLetteredMessage(
            DeadLetterSeq: 99L,
            SequenceNumber: 42L,
            TopicName: "orders",
            PartitionKey: "order-123",
            EventType: "OrderCreated",
            Headers: headers,
            Payload: payloadBytes,
            PayloadContentType: "application/json",
            EventDateTimeUtc: eventDate,
            EventOrdinal: 1,
            AttemptCount: 5,
            CreatedAtUtc: createdAt,
            DeadLetteredAtUtc: deadLetteredAt,
            LastError: "Max retry count exceeded");

        Assert.Equal(99L, msg.DeadLetterSeq);
        Assert.Equal(42L, msg.SequenceNumber);
        Assert.Equal("orders", msg.TopicName);
        Assert.Equal("order-123", msg.PartitionKey);
        Assert.Equal("OrderCreated", msg.EventType);
        Assert.Same(headers, msg.Headers);
        Assert.Equal(payloadBytes, msg.Payload);
        Assert.Equal("application/json", msg.PayloadContentType);
        Assert.Equal(eventDate, msg.EventDateTimeUtc);
        Assert.Equal(1, msg.EventOrdinal);
        Assert.Equal(5, msg.AttemptCount);
        Assert.Equal(createdAt, msg.CreatedAtUtc);
        Assert.Equal(deadLetteredAt, msg.DeadLetteredAtUtc);
        Assert.Equal("Max retry count exceeded", msg.LastError);
    }

    [Fact]
    public void DeadLetteredMessage_NullHeadersAndLastError_IsAllowed()
    {
        var msg = new DeadLetteredMessage(
            DeadLetterSeq: 1L,
            SequenceNumber: 1L,
            TopicName: "t",
            PartitionKey: "k",
            EventType: "E",
            Headers: null,
            Payload: Encoding.UTF8.GetBytes("{}"),
            PayloadContentType: "application/json",
            EventDateTimeUtc: DateTimeOffset.UtcNow,
            EventOrdinal: 0,
            AttemptCount: 0,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            DeadLetteredAtUtc: DateTimeOffset.UtcNow,
            LastError: null);

        Assert.Null(msg.Headers);
        Assert.Null(msg.LastError);
    }

    [Fact]
    public void DeadLetteredMessage_RecordEquality_ByteArrayUsesReferenceEquality()
    {
        var ts = DateTimeOffset.UtcNow;
        var payload = Encoding.UTF8.GetBytes("{}");
        var msg1 = new DeadLetteredMessage(1L, 1L, "t", "k", "E", null, payload, "application/json", ts, 0, 0, ts, ts, null);
        var msg2 = new DeadLetteredMessage(1L, 1L, "t", "k", "E", null, payload, "application/json", ts, 0, 0, ts, ts, null);
        var msg3 = new DeadLetteredMessage(1L, 1L, "t", "k", "E", null, Encoding.UTF8.GetBytes("{}"), "application/json", ts, 0, 0, ts, ts,
            null);
        var msg4 = new DeadLetteredMessage(2L, 1L, "t", "k", "E", null, payload, "application/json", ts, 0, 0, ts, ts, null);

        Assert.Equal(msg1, msg2); // same byte[] reference
        Assert.NotEqual(msg1, msg3); // different byte[] reference, same content
        Assert.NotEqual(msg1, msg4); // different DeadLetterSeq
    }

    [Fact]
    public void PartialSendException_PropertiesAreSet()
    {
        var succeeded = new List<long> { 1L, 2L };
        var failed = new List<long> { 3L, 4L };
        var inner = new InvalidOperationException("broker down");

        var ex = new PartialSendException(succeeded, failed, "partial send", inner);

        Assert.Equal(succeeded, ex.SucceededSequenceNumbers);
        Assert.Equal(failed, ex.FailedSequenceNumbers);
        Assert.Equal("partial send", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public async Task IOutboxEventHandler_DefaultImplementations_ReturnCompletedTask()
    {
        // Exercises the default interface method bodies on IOutboxEventHandler (lines with => Task.CompletedTask)
        IOutboxEventHandler handler = new MinimalEventHandler();

        var msg = new OutboxMessage(1L, "t", "k", "E", null, Encoding.UTF8.GetBytes("{}"), "application/json", DateTimeOffset.UtcNow, 0,
            DateTimeOffset.UtcNow);

        await handler.OnMessagePublishedAsync(msg, default);
        await handler.OnMessageDeadLetteredAsync(msg, default);
        await handler.OnPublishFailedAsync(new[] { msg }, new Exception("fail"), PublishFailureReason.RetriesExhausted, default);
        await handler.OnCircuitBreakerStateChangedAsync("topic", CircuitState.Open, default);
        await handler.OnRebalanceAsync("publisher-1", [0, 1], default);

        // All default implementations simply return Task.CompletedTask — reaching here means they completed successfully.
        Assert.True(true, "All default interface methods completed without throwing");
    }

    [Fact]
    public void OutboxPublisherOptions_GroupName_DefaultsToNull()
    {
        var opts = new OutboxPublisherOptions();
        Assert.Null(opts.GroupName);
    }

    private sealed class MinimalEventHandler : IOutboxEventHandler
    {
    }
}
