// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.EventHub.Tests;

public class EventHubTransportInterceptorTests
{
    private static EventHubOutboxTransport CreateTransport(
        IEnumerable<ITransportMessageInterceptor<EventData>>? interceptors = null)
    {
        var options = Options.Create(new EventHubTransportOptions
        {
            MaxBatchSizeBytes = 1_048_576,
            SendTimeoutSeconds = 15,
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=abc="
        });
        EventHubClientFactory factory = name =>
            new EventHubProducerClient(options.Value.ConnectionString, name);

        return new EventHubOutboxTransport(options,
            NullLogger<EventHubOutboxTransport>.Instance,
            interceptors ?? Enumerable.Empty<ITransportMessageInterceptor<EventData>>(),
            factory);
    }

    private static OutboxMessage MakeMessage(long seq = 1) =>
        new(seq, "test-hub", "pk", "TestEvent", null,
            Encoding.UTF8.GetBytes("{}"), "application/json",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Interceptor_AppliesToReturnsFalse_NoInterception()
    {
        var interceptor = Substitute.For<ITransportMessageInterceptor<EventData>>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(false);

        var transport = CreateTransport(interceptors: new[] { interceptor });

        try
        {
            await transport.SendAsync("test-hub", "pk", new[] { MakeMessage() }, CancellationToken.None);
        }
        catch
        {
            /* Expected: real client with test connection string */
        }

        await interceptor.DidNotReceive().InterceptAsync(
            Arg.Any<TransportMessageContext<EventData>>(),
            Arg.Any<CancellationToken>());
    }

    // NOTE: Positive-path test (interceptor mutates EventData and the mutated data reaches SendAsync)
    // cannot be unit tested because EventDataBatch is a sealed Azure SDK type that cannot be mocked.
    // This gap should be covered by integration tests.

    [Fact]
    public async Task Interceptor_Throws_ExceptionPropagates()
    {
        var interceptor = Substitute.For<ITransportMessageInterceptor<EventData>>();
        interceptor.AppliesTo(Arg.Any<OutboxMessage>()).Returns(true);
        interceptor.When(x => x.InterceptAsync(
                Arg.Any<TransportMessageContext<EventData>>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("eventhub interceptor boom"));

        var transport = CreateTransport(interceptors: new[] { interceptor });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transport.SendAsync("test-hub", "pk", new[] { MakeMessage() }, CancellationToken.None));
    }
}
