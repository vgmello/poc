// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

public sealed record DeadLetteredMessage(
    long DeadLetterSeq,
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    Dictionary<string, string>? Headers,
    byte[] Payload,
    string PayloadContentType,
    DateTimeOffset EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadLetteredAtUtc,
    string? LastError);
