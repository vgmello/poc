// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Kafka.Tests;

public class KafkaOutboxTransportSendTests
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly KafkaOutboxTransport _transport;

    public KafkaOutboxTransportSendTests()
    {
        _producer = Substitute.For<IProducer<string, byte[]>>();

        var options = Options.Create(new KafkaTransportOptions
        {
            SendTimeoutSeconds = 10,
            MaxBatchSizeBytes = 1_048_576,
            BootstrapServers = "localhost:9092",
            Acks = "All",
            EnableIdempotence = true
        });

        _transport = new KafkaOutboxTransport(
            _producer,
            options,
            NullLogger<KafkaOutboxTransport>.Instance,
            Enumerable.Empty<ITransportMessageInterceptor<Message<string, byte[]>>>());
    }

    private static OutboxMessage MakeMessage(long seq, string payload = "payload") =>
        new OutboxMessage(
            SequenceNumber: seq,
            TopicName: "test-topic",
            PartitionKey: "pk",
            EventType: "TestEvent",
            Headers: null,
            Payload: Encoding.UTF8.GetBytes(payload),
            PayloadContentType: "application/json",
            EventDateTimeUtc: DateTimeOffset.UtcNow,
            CreatedAtUtc: DateTimeOffset.UtcNow);

    private void SetupProducerSuccess()
    {
        _producer.When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<string, byte[]>>(),
                Arg.Any<Action<DeliveryReport<string, byte[]>>>()))
            .Do(ci =>
            {
                var callback = ci.ArgAt<Action<DeliveryReport<string, byte[]>>>(2);
                callback(new DeliveryReport<string, byte[]>
                {
                    Error = new Error(ErrorCode.NoError)
                });
            });
    }

    private void SetupProducerFailure()
    {
        _producer.When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<string, byte[]>>(),
                Arg.Any<Action<DeliveryReport<string, byte[]>>>()))
            .Do(ci =>
            {
                var callback = ci.ArgAt<Action<DeliveryReport<string, byte[]>>>(2);
                callback(new DeliveryReport<string, byte[]>
                {
                    Error = new Error(ErrorCode.BrokerNotAvailable)
                });
            });
    }

    [Fact]
    public async Task SendAsync_AllMessagesSucceed_DoesNotThrow()
    {
        SetupProducerSuccess();
        var messages = new List<OutboxMessage>
        {
            MakeMessage(1),
            MakeMessage(2),
            MakeMessage(3)
        };

        var exception = await Record.ExceptionAsync(() =>
            _transport.SendAsync("test-topic", "pk", messages, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendAsync_AllMessagesSucceed_ProduceCalledNTimes()
    {
        SetupProducerSuccess();
        var messages = new List<OutboxMessage>
        {
            MakeMessage(1),
            MakeMessage(2),
            MakeMessage(3)
        };

        await _transport.SendAsync("test-topic", "pk", messages, CancellationToken.None);

        _producer.Received(3).Produce(
            Arg.Any<string>(),
            Arg.Any<Message<string, byte[]>>(),
            Arg.Any<Action<DeliveryReport<string, byte[]>>>());
    }

    [Fact]
    public async Task SendAsync_AllMessagesFail_ThrowsAggregateException()
    {
        SetupProducerFailure();
        var messages = new List<OutboxMessage>
        {
            MakeMessage(1),
            MakeMessage(2)
        };

        var exception = await Record.ExceptionAsync(() =>
            _transport.SendAsync("test-topic", "pk", messages, CancellationToken.None));

        Assert.NotNull(exception);
        Assert.IsType<AggregateException>(exception);
    }

    [Fact]
    public async Task SendAsync_PartialDelivery_ThrowsPartialSendException()
    {
        // First message succeeds, second fails
        var callIndex = 0;
        _producer.When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<string, byte[]>>(),
                Arg.Any<Action<DeliveryReport<string, byte[]>>>()))
            .Do(ci =>
            {
                var callback = ci.ArgAt<Action<DeliveryReport<string, byte[]>>>(2);
                var idx = Interlocked.Increment(ref callIndex);
                var errorCode = idx == 1 ? ErrorCode.NoError : ErrorCode.BrokerNotAvailable;
                callback(new DeliveryReport<string, byte[]>
                {
                    Error = new Error(errorCode)
                });
            });

        var messages = new List<OutboxMessage>
        {
            MakeMessage(1),
            MakeMessage(2)
        };

        var exception = await Record.ExceptionAsync(() =>
            _transport.SendAsync("test-topic", "pk", messages, CancellationToken.None));

        Assert.NotNull(exception);
        var pex = Assert.IsType<PartialSendException>(exception);
        Assert.Contains(1L, pex.SucceededSequenceNumbers);
        Assert.Contains(2L, pex.FailedSequenceNumbers);
    }

    [Fact]
    public async Task SendAsync_PartialDelivery_SucceededCountMatchesSentMessages()
    {
        // Messages 1 and 3 succeed, message 2 fails
        var callIndex = 0;
        _producer.When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<string, byte[]>>(),
                Arg.Any<Action<DeliveryReport<string, byte[]>>>()))
            .Do(ci =>
            {
                var callback = ci.ArgAt<Action<DeliveryReport<string, byte[]>>>(2);
                var idx = Interlocked.Increment(ref callIndex);
                var errorCode = idx == 2 ? ErrorCode.BrokerNotAvailable : ErrorCode.NoError;
                callback(new DeliveryReport<string, byte[]>
                {
                    Error = new Error(errorCode)
                });
            });

        var messages = new List<OutboxMessage>
        {
            MakeMessage(1),
            MakeMessage(2),
            MakeMessage(3)
        };

        var exception = await Record.ExceptionAsync(() =>
            _transport.SendAsync("test-topic", "pk", messages, CancellationToken.None));

        Assert.NotNull(exception);
        var pex = Assert.IsType<PartialSendException>(exception);
        Assert.Equal(2, pex.SucceededSequenceNumbers.Count); // 2 succeeded
        Assert.Single(pex.FailedSequenceNumbers); // 1 failed
    }

    [Fact]
    public async Task SendAsync_MultipleSubBatches_SecondBatchFails_ThrowsPartialSendException()
    {
        // Use small MaxBatchSizeBytes to force sub-batching
        // Each message payload = "payload" (7 bytes) + key (2 bytes) + event type (9 bytes) + 100 overhead = ~118 bytes
        // Set limit to 200 so first message fits, second doesn't fit with first (200 < 118+118)
        var options = Options.Create(new KafkaTransportOptions
        {
            SendTimeoutSeconds = 10,
            MaxBatchSizeBytes = 200, // forces 1 message per sub-batch
            BootstrapServers = "localhost:9092",
            Acks = "All",
            EnableIdempotence = true
        });

        var transport = new KafkaOutboxTransport(
            _producer,
            options,
            NullLogger<KafkaOutboxTransport>.Instance,
            Enumerable.Empty<ITransportMessageInterceptor<Message<string, byte[]>>>());

        // First sub-batch (message 1) succeeds, second sub-batch (message 2) fails entirely
        var callIndex = 0;
        _producer.When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<string, byte[]>>(),
                Arg.Any<Action<DeliveryReport<string, byte[]>>>()))
            .Do(ci =>
            {
                var callback = ci.ArgAt<Action<DeliveryReport<string, byte[]>>>(2);
                var idx = Interlocked.Increment(ref callIndex);
                var errorCode = idx == 1 ? ErrorCode.NoError : ErrorCode.BrokerNotAvailable;
                callback(new DeliveryReport<string, byte[]>
                {
                    Error = new Error(errorCode)
                });
            });

        var messages = new List<OutboxMessage>
        {
            MakeMessage(1),
            MakeMessage(2)
        };

        var exception = await Record.ExceptionAsync(() =>
            transport.SendAsync("test-topic", "pk", messages, CancellationToken.None));

        Assert.NotNull(exception);
        var pex = Assert.IsType<PartialSendException>(exception);
        // First sub-batch message should be in succeeded
        Assert.Contains(1L, pex.SucceededSequenceNumbers);
        // Second sub-batch message should be in failed
        Assert.Contains(2L, pex.FailedSequenceNumbers);
    }

    [Fact]
    public async Task SendAsync_EmptyMessageList_DoesNotThrow()
    {
        SetupProducerSuccess();
        var messages = new List<OutboxMessage>();

        var exception = await Record.ExceptionAsync(() =>
            _transport.SendAsync("test-topic", "pk", messages, CancellationToken.None));

        Assert.Null(exception);
        _producer.DidNotReceive().Produce(
            Arg.Any<string>(),
            Arg.Any<Message<string, byte[]>>(),
            Arg.Any<Action<DeliveryReport<string, byte[]>>>());
    }
}
