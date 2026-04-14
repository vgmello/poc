// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Kafka.Tests;

public class KafkaMessageHelperTests
{
    private static OutboxMessage MakeMessage(long seq, string payload = "payload",
        Dictionary<string, string>? headers = null, string eventType = "TestEvent") =>
        new OutboxMessage(
            SequenceNumber: seq,
            TopicName: "test-topic",
            PartitionKey: "pk",
            EventType: eventType,
            Headers: headers,
            Payload: Encoding.UTF8.GetBytes(payload),
            PayloadContentType: "application/json",
            EventDateTimeUtc: DateTimeOffset.UtcNow,
            EventOrdinal: 0,
            CreatedAtUtc: DateTimeOffset.UtcNow);

    // ── SplitIntoSubBatches ────────────────────────────────────────────────

    [Fact]
    public void SplitIntoSubBatches_SingleSmallMessage_ReturnsSingleBatchWithOneMessage()
    {
        var messages = new List<OutboxMessage> { MakeMessage(1) };

        var result = KafkaMessageHelper.SplitIntoSubBatches("pk", messages, maxBatchSizeBytes: 1_048_576);

        Assert.Single(result);
        Assert.Single(result[0]);
    }

    [Fact]
    public void SplitIntoSubBatches_MultipleMessagesUnderLimit_ReturnsSingleBatch()
    {
        var messages = Enumerable.Range(1, 5)
            .Select(i => MakeMessage(i, "small"))
            .ToList();

        var result = KafkaMessageHelper.SplitIntoSubBatches("pk", messages, maxBatchSizeBytes: 1_048_576);

        Assert.Single(result);
        Assert.Equal(5, result[0].Count);
    }

    [Fact]
    public void SplitIntoSubBatches_MessagesExceedingLimit_SplitsIntoMultipleBatchesPreservingTotal()
    {
        // Each message is ~200 bytes (100 framing + payload); use a tight limit so they split
        var bigPayload = new string('x', 400);
        var messages = Enumerable.Range(1, 5)
            .Select(i => MakeMessage(i, bigPayload))
            .ToList();

        // Limit of 600 bytes: each message ~500 bytes, so at most 1 per batch
        var result = KafkaMessageHelper.SplitIntoSubBatches("pk", messages, maxBatchSizeBytes: 600);

        var totalMessages = result.Sum(b => b.Count);
        Assert.Equal(5, totalMessages);
        Assert.True(result.Count > 1, "Expected messages to be split across multiple batches");
    }

    [Fact]
    public void SplitIntoSubBatches_SingleOversizeMessage_GetsItsOwnBatchAndIsNotDropped()
    {
        var oversizePayload = new string('x', 2_000_000); // 2 MB payload
        var messages = new List<OutboxMessage> { MakeMessage(1, oversizePayload) };

        // maxBatchSizeBytes is 1 MB — message exceeds it
        var result = KafkaMessageHelper.SplitIntoSubBatches("pk", messages, maxBatchSizeBytes: 1_048_576);

        Assert.Single(result);
        Assert.Single(result[0]);
        Assert.Equal(1, result[0][0].SequenceNumber);
    }

    [Fact]
    public void SplitIntoSubBatches_MessagesAtExactBoundary_DoesNotSplitUnnecessarily()
    {
        // Build a message whose size we know, then set the limit just above it
        const string partitionKey = "pk";
        var msg = MakeMessage(1, "payload");
        var msgSize = KafkaMessageHelper.EstimateMessageSize(partitionKey, msg);

        // Two messages exactly at 2× size — limit is 2× + 1, so both fit in one batch
        var messages = new List<OutboxMessage> { MakeMessage(1, "payload"), MakeMessage(2, "payload") };
        var result = KafkaMessageHelper.SplitIntoSubBatches(partitionKey, messages, maxBatchSizeBytes: msgSize * 2 + 1);

        Assert.Single(result);
        Assert.Equal(2, result[0].Count);
    }

    // ── EstimateMessageSize ────────────────────────────────────────────────

    [Fact]
    public void EstimateMessageSize_MessageWithAllFields_ReturnsPositiveValue()
    {
        var msg = MakeMessage(1, "payload", headers: new Dictionary<string, string> { { "key", "value" } }, eventType: "SomeEvent");

        var size = KafkaMessageHelper.EstimateMessageSize("partition-key", msg);

        Assert.True(size > 0);
    }

    [Fact]
    public void EstimateMessageSize_NullHeaders_StillReturnsPositiveValue()
    {
        var msg = MakeMessage(1, "payload", headers: null);

        var size = KafkaMessageHelper.EstimateMessageSize("pk", msg);

        Assert.True(size > 0);
    }

    [Fact]
    public void EstimateMessageSize_LargePayload_ReturnsLargerSizeThanSmallPayload()
    {
        var smallMsg = MakeMessage(1, "hi");
        var largeMsg = MakeMessage(2, new string('x', 50_000));

        var smallSize = KafkaMessageHelper.EstimateMessageSize("pk", smallMsg);
        var largeSize = KafkaMessageHelper.EstimateMessageSize("pk", largeMsg);

        Assert.True(largeSize > smallSize);
    }

    // ── ParseHeaders ──────────────────────────────────────────────────────

    [Fact]
    public void ParseHeaders_ValidJson_ReturnsHeadersWithEntries()
    {
        var dict = new Dictionary<string, string> { { "CorrelationId", "abc" }, { "Source", "svc" } };

        var headers = KafkaMessageHelper.ParseHeaders(dict, "OrderCreated");

        // At minimum: the two user headers + system EventType
        Assert.True(headers.Count >= 3);
        Assert.Contains(headers, h => h.Key == "CorrelationId");
        Assert.Contains(headers, h => h.Key == "Source");
        Assert.Contains(headers, h => h.Key == "EventType");
    }

    [Fact]
    public void ParseHeaders_NullOrEmpty_ReturnsHeadersWithEventTypeOnly()
    {
        var headersNull = KafkaMessageHelper.ParseHeaders(null, "OrderCreated");
        var headersEmpty = KafkaMessageHelper.ParseHeaders([], "OrderCreated");

        Assert.Single(headersNull);
        Assert.Equal("EventType", headersNull[0].Key);

        Assert.Single(headersEmpty);
        Assert.Equal("EventType", headersEmpty[0].Key);
    }

    [Fact]
    public void ParseHeaders_JsonContainsEventTypeKey_SkipsItAndUsesSystemEventType()
    {
        // User-supplied EventType should be ignored; system value wins
        var dict = new Dictionary<string, string> { { "EventType", "UserValue" }, { "Other", "val" } };

        var headers = KafkaMessageHelper.ParseHeaders(dict, "SystemEvent");

        var eventTypeHeaders = headers.Where(h => h.Key == "EventType").ToList();
        Assert.Single(eventTypeHeaders); // exactly one EventType header
        Assert.Equal("SystemEvent",
            Encoding.UTF8.GetString(eventTypeHeaders[0].GetValueBytes()));
    }
}
