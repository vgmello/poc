// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Mutable context passed to <see cref="Abstractions.IOutboxMessageInterceptor" /> implementations.
///     Only created when at least one interceptor's <c>AppliesTo</c> returns true.
/// </summary>
public sealed class OutboxMessageContext
{
    private readonly OutboxMessage _original;

    // Read-only metadata — routing/ordering invariants preserved
    public long SequenceNumber => _original.SequenceNumber;
    public string TopicName => _original.TopicName;
    public string PartitionKey => _original.PartitionKey;
    public string EventType => _original.EventType;
    public DateTimeOffset EventDateTimeUtc => _original.EventDateTimeUtc;
    public int EventOrdinal => _original.EventOrdinal;
    public DateTimeOffset CreatedAtUtc => _original.CreatedAtUtc;

    // Mutable fields
    private byte[] _payload;

    public byte[] Payload
    {
        get => _payload;
        set => _payload = value ?? throw new ArgumentNullException(nameof(value), "Payload cannot be set to null.");
    }

    public Dictionary<string, string>? Headers { get; set; }
    public string PayloadContentType { get; set; }

    internal OutboxMessageContext(OutboxMessage message)
    {
        _original = message;
        _payload = message.Payload;
        Headers = message.Headers is not null ? new Dictionary<string, string>(message.Headers) : null;
        PayloadContentType = message.PayloadContentType;
    }

    internal OutboxMessage ToOutboxMessage() =>
        _original with
        {
            Payload = Payload,
            Headers = Headers,
            PayloadContentType = PayloadContentType
        };
}
