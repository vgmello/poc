// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Outbox.Core.Models;

namespace Outbox.Kafka;

internal static class KafkaMessageHelper
{
    internal static List<List<OutboxMessage>> SplitIntoSubBatches(
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        int maxBatchSizeBytes)
    {
        var subBatches = new List<List<OutboxMessage>>();
        var current = new List<OutboxMessage>();
        var currentSize = 0;

        foreach (var msg in messages)
        {
            var msgSize = EstimateMessageSize(partitionKey, msg);

            // Single message exceeds max — flush current batch first, then send it alone
            // (let Kafka reject it via delivery report if truly oversized)
            if (msgSize >= maxBatchSizeBytes && current.Count > 0)
            {
                subBatches.Add(current);
                current = [];
                currentSize = 0;
            }

            if (currentSize + msgSize > maxBatchSizeBytes && current.Count > 0)
            {
                subBatches.Add(current);
                current = [];
                currentSize = 0;
            }

            current.Add(msg);
            currentSize += msgSize;
        }

        if (current.Count > 0)
            subBatches.Add(current);

        return subBatches;
    }

    internal static int EstimateMessageSize(string partitionKey, OutboxMessage msg)
    {
        var size = System.Text.Encoding.UTF8.GetByteCount(partitionKey)
                   + msg.Payload.Length
                   + System.Text.Encoding.UTF8.GetByteCount(msg.EventType)
                   + 100; // protocol framing overhead

        if (msg.Headers is not null)
        {
            foreach (var kvp in msg.Headers)
            {
                size += System.Text.Encoding.UTF8.GetByteCount(kvp.Key)
                        + System.Text.Encoding.UTF8.GetByteCount(kvp.Value)
                        + 20; // per-header Kafka framing
            }
        }

        return size;
    }

    internal static Headers ParseHeaders(Dictionary<string, string>? headers, string eventType)
    {
        var kafkaHeaders = new Headers();

        if (headers is not null)
        {
            foreach (var kvp in headers)
            {
                if (!string.Equals(kvp.Key, "EventType", StringComparison.OrdinalIgnoreCase))
                    kafkaHeaders.Add(kvp.Key, System.Text.Encoding.UTF8.GetBytes(kvp.Value));
            }
        }

        kafkaHeaders.Add("EventType", System.Text.Encoding.UTF8.GetBytes(eventType));

        return kafkaHeaders;
    }
}
