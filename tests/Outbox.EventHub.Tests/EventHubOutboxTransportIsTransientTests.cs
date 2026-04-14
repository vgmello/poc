// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Xunit;

namespace Outbox.EventHub.Tests;

public sealed class EventHubOutboxTransportIsTransientTests
{
    private readonly EventHubOutboxTransport _transport;

    public EventHubOutboxTransportIsTransientTests()
    {
        var options = Options.Create(new EventHubTransportOptions
        {
            SendTimeoutSeconds = 30,
            MaxBatchSizeBytes = 1_000_000
        });
        var clientFactory = new EventHubClientFactory(_ => Substitute.For<EventHubProducerClient>());
        _transport = new EventHubOutboxTransport(
            options,
            NullLogger<EventHubOutboxTransport>.Instance,
            Array.Empty<ITransportMessageInterceptor<EventData>>(),
            clientFactory);
    }

    [Fact]
    public void IsTransient_True_WhenEventHubsExceptionIsTransient()
    {
        var ex = new EventHubsException(
            isTransient: true,
            eventHubName: "test-hub",
            message: "broker timeout");
        Assert.True(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_False_WhenEventHubsExceptionIsNotTransient()
    {
        var ex = new EventHubsException(
            isTransient: false,
            eventHubName: "test-hub",
            message: "auth failed");
        Assert.False(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_True_ForOperationCanceledException()
    {
        Assert.True(_transport.IsTransient(new OperationCanceledException()));
    }

    [Fact]
    public void IsTransient_True_ForTimeoutException()
    {
        Assert.True(_transport.IsTransient(new TimeoutException("send timed out")));
    }

    [Fact]
    public void IsTransient_False_ForInvalidOperationException()
    {
        // E.g., "message too large for batch" — message is bad, not transport.
        Assert.False(_transport.IsTransient(new InvalidOperationException("too large")));
    }

    [Fact]
    public void IsTransient_True_ForAggregateException_AllInnerTransient()
    {
        var inner1 = new EventHubsException(true, "h", "t1");
        var inner2 = new TimeoutException("t2");

        Assert.True(_transport.IsTransient(new AggregateException(inner1, inner2)));
    }

    [Fact]
    public void IsTransient_False_ForAggregateException_AnyInnerNonTransient()
    {
        var transient = new EventHubsException(true, "h", "t");
        var poison = new InvalidOperationException("bad");

        Assert.False(_transport.IsTransient(new AggregateException(transient, poison)));
    }

    [Fact]
    public void IsTransient_False_ForUnknownExceptionType()
    {
        Assert.False(_transport.IsTransient(new ApplicationException("???")));
    }
}
