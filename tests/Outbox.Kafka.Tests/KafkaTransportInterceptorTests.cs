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

public class KafkaTransportInterceptorTests
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly KafkaTransportOptions _transportOptions;

    public KafkaTransportInterceptorTests()
    {
        _producer = Substitute.For<IProducer<string, byte[]>>();

        _transportOptions = new KafkaTransportOptions
        {
            SendTimeoutSeconds = 10,
            MaxBatchSizeBytes = 1_048_576,
            BootstrapServers = "localhost:9092",
            Acks = "All",
            EnableIdempotence = true
        };

        // Simulate successful produce: invoke callback with success
        _producer.When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<string, byte[]>>(),
                Arg.Any<Action<DeliveryReport<string, byte[]>>>()))
            .Do(ci =>
            {
                var callback = ci.Arg<Action<DeliveryReport<string, byte[]>>>();
                callback(new DeliveryReport<string, byte[]> { Error = new Error(ErrorCode.NoError) });
            });
    }

    private static OutboxMessage MakeMessage(long seq) =>
        new(seq, "test-topic", "pk", "TestEvent", null,
            Encoding.UTF8.GetBytes("payload"), "application/json",
            DateTimeOffset.UtcNow, 0, 0, DateTimeOffset.UtcNow);

    private KafkaOutboxTransport CreateTransport(
        IEnumerable<ITransportMessageInterceptor<Message<string, byte[]>>>? interceptors = null) =>
        new(_producer, Options.Create(_transportOptions),
            NullLogger<KafkaOutboxTransport>.Instance,
            interceptors ?? Enumerable.Empty<ITransportMessageInterceptor<Message<string, byte[]>>>());

    [Fact]
    public async Task NoInterceptors_MessageProducedNormally()
    {
        var transport = CreateTransport();
        await transport.SendAsync("test-topic", "pk", new[] { MakeMessage(1) }, CancellationToken.None);

        _producer.Received(1).Produce(
            "test-topic",
            Arg.Is<Message<string, byte[]>>(m => Encoding.UTF8.GetString(m.Value) == "payload"),
            Arg.Any<Action<DeliveryReport<string, byte[]>>>());
    }

    [Fact]
    public async Task Interceptor_MutatesEnvelope_ProducerReceivesMutatedMessage()
    {
        var interceptor = Substitute.For<ITransportMessageInterceptor<Message<string, byte[]>>>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(
                Arg.Any<TransportMessageContext<Message<string, byte[]>>>(),
                Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var ctx = ci.Arg<TransportMessageContext<Message<string, byte[]>>>();
                ctx.Message.Headers.Add("X-Custom", Encoding.UTF8.GetBytes("custom-value"));
            });

        Message<string, byte[]>? capturedMessage = null;
        _producer.When(p => p.Produce(
                Arg.Any<string>(),
                Arg.Any<Message<string, byte[]>>(),
                Arg.Any<Action<DeliveryReport<string, byte[]>>>()))
            .Do(ci =>
            {
                capturedMessage = ci.ArgAt<Message<string, byte[]>>(1);
            });

        var transport = CreateTransport(new[] { interceptor });
        await transport.SendAsync("test-topic", "pk", new[] { MakeMessage(1) }, CancellationToken.None);

        Assert.NotNull(capturedMessage);
        Assert.True(capturedMessage.Headers.TryGetLastBytes("X-Custom", out var val));
        Assert.Equal("custom-value", Encoding.UTF8.GetString(val));
    }

    [Fact]
    public async Task Interceptor_AppliesToReturnsFalse_NoInterception()
    {
        var interceptor = Substitute.For<ITransportMessageInterceptor<Message<string, byte[]>>>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(false);

        var transport = CreateTransport(new[] { interceptor });
        await transport.SendAsync("test-topic", "pk", new[] { MakeMessage(1) }, CancellationToken.None);

        await interceptor.DidNotReceive().InterceptAsync(
            Arg.Any<TransportMessageContext<Message<string, byte[]>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_Throws_ExceptionPropagates()
    {
        var interceptor = Substitute.For<ITransportMessageInterceptor<Message<string, byte[]>>>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(
                Arg.Any<TransportMessageContext<Message<string, byte[]>>>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("transport interceptor boom"));

        var transport = CreateTransport(new[] { interceptor });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync("test-topic", "pk", new[] { MakeMessage(1) }, CancellationToken.None));

        // Producer should NOT have been called since interceptor threw before Produce.
        _producer.DidNotReceive().Produce(
            Arg.Any<string>(),
            Arg.Any<Message<string, byte[]>>(),
            Arg.Any<Action<DeliveryReport<string, byte[]>>>());
    }
}
