// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Xunit;

namespace Outbox.Kafka.Tests;

public sealed class KafkaOutboxTransportIsTransientTests
{
    private readonly KafkaOutboxTransport _transport;

    public KafkaOutboxTransportIsTransientTests()
    {
        var producer = Substitute.For<IProducer<string, byte[]>>();
        var options = Options.Create(new KafkaTransportOptions
        {
            SendTimeoutSeconds = 30,
            MaxBatchSizeBytes = 1_000_000
        });
        _transport = new KafkaOutboxTransport(
            producer,
            options,
            NullLogger<KafkaOutboxTransport>.Instance,
            Array.Empty<ITransportMessageInterceptor<Message<string, byte[]>>>());
    }

    [Theory]
    [InlineData(ErrorCode.BrokerNotAvailable)]
    [InlineData(ErrorCode.NetworkException)]
    [InlineData(ErrorCode.RequestTimedOut)]
    [InlineData(ErrorCode.LeaderNotAvailable)]
    [InlineData(ErrorCode.NotLeaderForPartition)]
    [InlineData(ErrorCode.NotEnoughReplicas)]
    [InlineData(ErrorCode.NotEnoughReplicasAfterAppend)]
    [InlineData(ErrorCode.OutOfOrderSequenceNumber)]
    [InlineData(ErrorCode.Local_Transport)]
    [InlineData(ErrorCode.Local_AllBrokersDown)]
    [InlineData(ErrorCode.Local_TimedOut)]
    [InlineData(ErrorCode.Local_QueueFull)]
    [InlineData(ErrorCode.Local_MsgTimedOut)]
    public void IsTransient_True_ForKnownTransientCodes(ErrorCode code)
    {
        var ex = new ProduceException<string, byte[]>(
            new Error(code, "test"),
            new DeliveryResult<string, byte[]>());
        Assert.True(_transport.IsTransient(ex));
    }

    [Theory]
    [InlineData(ErrorCode.MsgSizeTooLarge)]
    [InlineData(ErrorCode.InvalidMsg)]
    [InlineData(ErrorCode.UnknownTopicOrPart)]
    [InlineData(ErrorCode.TopicAuthorizationFailed)]
    [InlineData(ErrorCode.ClusterAuthorizationFailed)]
    [InlineData(ErrorCode.PolicyViolation)]
    public void IsTransient_False_ForKnownNonTransientCodes(ErrorCode code)
    {
        var ex = new ProduceException<string, byte[]>(
            new Error(code, "test"),
            new DeliveryResult<string, byte[]>());
        Assert.False(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_True_ForAggregateException_AllInnerTransient()
    {
        var inner1 = new ProduceException<string, byte[]>(
            new Error(ErrorCode.NetworkException, "n"),
            new DeliveryResult<string, byte[]>());
        var inner2 = new ProduceException<string, byte[]>(
            new Error(ErrorCode.RequestTimedOut, "t"),
            new DeliveryResult<string, byte[]>());

        Assert.True(_transport.IsTransient(new AggregateException(inner1, inner2)));
    }

    [Fact]
    public void IsTransient_False_ForAggregateException_AnyInnerNonTransient()
    {
        var transient = new ProduceException<string, byte[]>(
            new Error(ErrorCode.NetworkException, "n"),
            new DeliveryResult<string, byte[]>());
        var poison = new ProduceException<string, byte[]>(
            new Error(ErrorCode.MsgSizeTooLarge, "p"),
            new DeliveryResult<string, byte[]>());

        Assert.False(_transport.IsTransient(new AggregateException(transient, poison)));
    }

    [Fact]
    public void IsTransient_False_ForUnknownExceptionType()
    {
        Assert.False(_transport.IsTransient(new InvalidOperationException("???")));
    }

    [Fact]
    public void IsTransient_True_ForKafkaException_TransientCode()
    {
        var ex = new KafkaException(new Error(ErrorCode.NetworkException, "n"));
        Assert.True(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_True_ForOperationCanceledException()
    {
        // Cancellation during a send is treated as transient — the message will be retried
        // on the next poll, not dead-lettered.
        Assert.True(_transport.IsTransient(new OperationCanceledException()));
    }
}
