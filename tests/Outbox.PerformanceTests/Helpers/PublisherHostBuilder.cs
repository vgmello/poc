using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Outbox.Core.Builder;
using Outbox.Core.Options;
using Outbox.EventHub;
using Outbox.Kafka;
using Outbox.PostgreSQL;
using Outbox.SqlServer;

namespace Outbox.PerformanceTests.Helpers;

public static class PublisherHostBuilder
{
    public static IHost Build(
        TestCombination combo,
        string pgConnectionString,
        string sqlServerConnectionString,
        string bootstrapServers,
        string eventHubConnectionString,
        Action<OutboxPublisherOptions>? configureOptions = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Outbox:Publisher:BatchSize"] = "500"
                });
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddOutbox(ctx.Configuration, outbox =>
                {
                    // Store
                    ConfigureStore(outbox, combo.Store, pgConnectionString, sqlServerConnectionString);

                    // Transport
                    ConfigureTransport(outbox, combo.Transport, bootstrapServers, eventHubConnectionString);

                    // Publisher options
                    outbox.ConfigurePublisher(o =>
                    {
                        PerfTestOptions.Default(o);
                        configureOptions?.Invoke(o);
                    });
                });
            })
            .Build();
    }

    private static void ConfigureStore(IOutboxBuilder outbox, StoreType store,
        string pgConnectionString, string sqlServerConnectionString)
    {
        switch (store)
        {
            case StoreType.PostgreSql:
                outbox.UsePostgreSql(options =>
                {
                    options.ConnectionString = pgConnectionString;
                });
                break;
            case StoreType.SqlServer:
                outbox.UseSqlServer(options =>
                {
                    options.ConnectionString = sqlServerConnectionString;
                });
                break;
        }
    }

    private static void ConfigureTransport(IOutboxBuilder outbox, TransportType transport,
        string bootstrapServers, string eventHubConnectionString)
    {
        switch (transport)
        {
            case TransportType.Redpanda:
                outbox.UseKafka(options =>
                {
                    options.BootstrapServers = bootstrapServers;
                    options.Acks = "All";
                    options.LingerMs = 5;
                });
                break;
            case TransportType.EventHub:
                outbox.UseEventHub(options =>
                {
                    options.ConnectionString = eventHubConnectionString;
                    // Emulator AMQP initialization is slow — increase from 15s default
                    options.SendTimeoutSeconds = 120;
                });
                break;
        }
    }
}
