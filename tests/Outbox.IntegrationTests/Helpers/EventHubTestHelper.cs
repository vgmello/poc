// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;
using Outbox.EventHub;
using Outbox.PostgreSQL;
using Outbox.SqlServer;

namespace Outbox.IntegrationTests.Helpers;

public sealed record ConsumedEventHubMessage(
    string PartitionKey,
    byte[] Body,
    long SequenceNumber,
    string PartitionId);

/// <summary>
///     Helpers for EventHub-backed integration tests. Mirrors OutboxTestHelper's
///     consumer-and-topic helpers but targets the Event Hubs emulator.
/// </summary>
public static class EventHubTestHelper
{
    /// <summary>
    ///     Size of the generic hub pool declared in Config.json.
    ///     CheckoutHub uses this value as its round-robin modulus so that the
    ///     emulator config and the checkout logic cannot drift out of sync.
    /// </summary>
    public const int PoolSize = 8;

    private static int _counter;

    // ------------------------------------------------------------------
    // Host builder
    // ------------------------------------------------------------------

    public static (IHost Host, EventHubFaultyTransportWrapper Transport) BuildEventHubPublisherHost(
        string connectionString,
        string eventHubConnectionString,
        Action<OutboxPublisherOptions>? configureOptions = null,
        ToggleableConnectionFactory? connectionFactory = null,
        bool useSqlServer = false)
    {
        connectionFactory ??= new ToggleableConnectionFactory(connectionString);
        var sqlServerConnectionFactory = useSqlServer
            ? new SqlServerToggleableConnectionFactory(connectionString)
            : null;

        ServiceDescriptor? realTransportDescriptor = null;

        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Outbox:Publisher:BatchSize"] = "10"
                });
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddOutbox(ctx.Configuration, outbox =>
                {
                    if (useSqlServer)
                    {
                        outbox.UseSqlServer(options =>
                        {
                            options.ConnectionFactory = sqlServerConnectionFactory!.CreateConnectionAsync;
                        });
                    }
                    else
                    {
                        outbox.UsePostgreSql(options =>
                        {
                            options.ConnectionFactory = connectionFactory.CreateConnectionAsync;
                        });
                    }
                    outbox.UseEventHub(ehOpts =>
                    {
                        ehOpts.ConnectionString = eventHubConnectionString;
                        ehOpts.SendTimeoutSeconds = 5;
                    });
                    outbox.ConfigurePublisher(o =>
                    {
                        OutboxTestHelper.FastTestOptions(o);
                        configureOptions?.Invoke(o);
                    });
                });

                // Decorate the real transport with the fault wrapper.
                // UseEventHub registered EventHubOutboxTransport as IOutboxTransport
                // via TryAddSingleton. We remove that, re-register the concrete type
                // under itself (so DI can still resolve its dependencies), then add
                // the wrapper as the new IOutboxTransport.
                realTransportDescriptor = services.FirstOrDefault(
                    d => d.ServiceType == typeof(IOutboxTransport));
                if (realTransportDescriptor?.ImplementationType != null)
                {
                    services.Remove(realTransportDescriptor);

                    // Re-register the concrete internal type under itself.
                    services.AddSingleton(
                        realTransportDescriptor.ImplementationType,
                        realTransportDescriptor.ImplementationType);

                    services.AddSingleton<EventHubFaultyTransportWrapper>(sp =>
                    {
                        var realTransport = (IOutboxTransport)sp.GetRequiredService(
                            realTransportDescriptor.ImplementationType);
                        return new EventHubFaultyTransportWrapper(realTransport);
                    });

                    services.AddSingleton<IOutboxTransport>(sp =>
                        sp.GetRequiredService<EventHubFaultyTransportWrapper>());
                }
            })
            .Build();

        var transport = host.Services.GetRequiredService<EventHubFaultyTransportWrapper>();

        return (host, transport);
    }

    /// <summary>Round-robin checkout from the generic 4-partition hub pool.</summary>
    public static string CheckoutHub()
    {
        var index = Interlocked.Increment(ref _counter) % PoolSize;
        return $"test-hub-{index:D2}";
    }

    /// <summary>
    ///     Returns a named hub that exists in Config.json for tests with
    ///     specific partition-count requirements. Valid names:
    ///     "test-hub-ordering-8p", "test-hub-rebalance-16p".
    /// </summary>
    public static string CheckoutNamedHub(string name) => name;

    // ------------------------------------------------------------------
    // Consumer — pulls events back off the emulator for assertion
    // ------------------------------------------------------------------

    public static async Task<List<ConsumedEventHubMessage>> ConsumeMessagesAsync(
        string connectionString,
        string eventHubName,
        int expectedCount,
        TimeSpan timeout,
        string? partitionKeyFilter = null)
    {
        var messages = new List<ConsumedEventHubMessage>();

        await using var consumer = new EventHubConsumerClient(
            EventHubConsumerClient.DefaultConsumerGroupName,
            connectionString,
            eventHubName);

        var readOptions = new ReadEventOptions
        {
            MaximumWaitTime = TimeSpan.FromMilliseconds(200)
        };

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await foreach (var partitionEvent in consumer.ReadEventsAsync(
                               startReadingAtEarliestEvent: true, readOptions, cts.Token))
            {
                if (partitionEvent.Data is null)
                    continue;

                if (partitionKeyFilter is not null &&
                    partitionEvent.Data.PartitionKey != partitionKeyFilter)
                    continue;

                messages.Add(new ConsumedEventHubMessage(
                    partitionEvent.Data.PartitionKey ?? string.Empty,
                    partitionEvent.Data.EventBody.ToArray(),
                    partitionEvent.Data.SequenceNumber,
                    partitionEvent.Partition.PartitionId));

                if (messages.Count >= expectedCount)
                    break;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Deadline hit — return what we've collected so far. The caller asserts on the count.
        }

        return messages;
    }

    // ------------------------------------------------------------------
    // Drain — called in test teardown to evict residual messages so the next
    // test reusing the same pool slot sees an empty hub.
    // ------------------------------------------------------------------

    public static async Task DrainHubAsync(
        string connectionString, string eventHubName, TimeSpan timeout)
    {
        await using var consumer = new EventHubConsumerClient(
            EventHubConsumerClient.DefaultConsumerGroupName,
            connectionString,
            eventHubName);

        var readOptions = new ReadEventOptions
        {
            MaximumWaitTime = TimeSpan.FromMilliseconds(500)
        };

        using var cts = new CancellationTokenSource(timeout);
        var quietSince = DateTime.UtcNow;

        try
        {
            await foreach (var partitionEvent in consumer.ReadEventsAsync(
                               startReadingAtEarliestEvent: true, readOptions, cts.Token))
            {
                if (partitionEvent.Data is not null)
                    quietSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(500))
                    return;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Deadline hit — give up. Caller logs it, doesn't fail the test.
        }
    }
}
