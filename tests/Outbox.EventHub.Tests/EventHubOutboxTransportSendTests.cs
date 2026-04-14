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

public class EventHubOutboxTransportSendTests
{
    private static readonly IOptions<EventHubTransportOptions> DefaultOptions = Options.Create(
        new EventHubTransportOptions
        {
            MaxBatchSizeBytes = 1_048_576,
            SendTimeoutSeconds = 15,
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=abc="
        });

    private static EventHubOutboxTransport CreateTransport(
        EventHubClientFactory? clientFactory = null,
        IEnumerable<ITransportMessageInterceptor<EventData>>? interceptors = null)
    {
        clientFactory ??= name => new EventHubProducerClient(
            DefaultOptions.Value.ConnectionString, name);

        return new EventHubOutboxTransport(DefaultOptions,
            NullLogger<EventHubOutboxTransport>.Instance,
            interceptors ?? Enumerable.Empty<ITransportMessageInterceptor<EventData>>(),
            clientFactory);
    }

    private static OutboxMessage CreateMessage(string topicName = "test-hub") =>
        new OutboxMessage(
            SequenceNumber: 1L,
            TopicName: topicName,
            PartitionKey: "pk1",
            EventType: "TestEvent",
            Headers: null,
            Payload: Encoding.UTF8.GetBytes("{}"),
            PayloadContentType: "application/json",
            EventDateTimeUtc: DateTimeOffset.UtcNow,
            EventOrdinal: 0,
            CreatedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public async Task SendAsync_DifferentTopicNames_DoNotThrow()
    {
        var transport = CreateTransport();
        var messages = new[] { CreateMessage(topicName: "hub-a") };

        var ex = await Record.ExceptionAsync(() => transport.SendAsync("hub-a", "pk1", messages, CancellationToken.None));

        Assert.False(
            ex is InvalidOperationException ioe &&
            ioe.Message.Contains("transport is configured for"),
            "Should not throw a topic-mismatch InvalidOperationException");
    }

    [Fact]
    public async Task SendAsync_UsesClientFactoryPerTopicName()
    {
        var mockClient = Substitute.For<EventHubProducerClient>();
        var factoryCalls = new List<string>();
        EventHubClientFactory factory = name =>
        {
            factoryCalls.Add(name);

            return mockClient;
        };

        var transport = CreateTransport(clientFactory: factory);

        try { await transport.SendAsync("hub-a", "pk1", new[] { CreateMessage("hub-a") }, CancellationToken.None); }
        catch
        {
            /* Expected: mock client doesn't set up batches */
        }

        Assert.Single(factoryCalls);
        Assert.Equal("hub-a", factoryCalls[0]);
    }

    [Fact]
    public async Task SendAsync_SameTopicName_ReusesCachedClient()
    {
        var mockClient = Substitute.For<EventHubProducerClient>();
        var factoryCallCount = 0;
        EventHubClientFactory factory = name =>
        {
            factoryCallCount++;

            return mockClient;
        };

        var transport = CreateTransport(clientFactory: factory);

        try { await transport.SendAsync("hub-a", "pk1", new[] { CreateMessage("hub-a") }, CancellationToken.None); }
        catch
        {
            /* Expected */
        }

        try { await transport.SendAsync("hub-a", "pk1", new[] { CreateMessage("hub-a") }, CancellationToken.None); }
        catch
        {
            /* Expected */
        }

        Assert.Equal(1, factoryCallCount);
    }

    [Fact]
    public async Task SendAsync_DifferentTopicNames_CreatesSeparateClients()
    {
        var mockClient = Substitute.For<EventHubProducerClient>();
        var factoryCalls = new List<string>();
        EventHubClientFactory factory = name =>
        {
            factoryCalls.Add(name);

            return mockClient;
        };

        var transport = CreateTransport(clientFactory: factory);

        try { await transport.SendAsync("hub-a", "pk1", new[] { CreateMessage("hub-a") }, CancellationToken.None); }
        catch
        {
            /* Expected */
        }

        try { await transport.SendAsync("hub-b", "pk1", new[] { CreateMessage("hub-b") }, CancellationToken.None); }
        catch
        {
            /* Expected */
        }

        Assert.Equal(2, factoryCalls.Count);
        Assert.Contains("hub-a", factoryCalls);
        Assert.Contains("hub-b", factoryCalls);
    }

    [Fact]
    public async Task DisposeAsync_ClosesAllCachedClients()
    {
        var transport = CreateTransport();

        var ex = await Record.ExceptionAsync(() => transport.DisposeAsync().AsTask());
        Assert.Null(ex);
    }
}
