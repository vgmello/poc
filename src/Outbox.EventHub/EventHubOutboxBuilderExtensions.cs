using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.EventHub;

public static class EventHubOutboxBuilderExtensions
{
    public static IOutboxBuilder UseEventHub(
        this IOutboxBuilder builder,
        Action<EventHubTransportOptions>? configure = null)
    {
        builder.Services.Configure<EventHubTransportOptions>(
            builder.Configuration.GetSection("Outbox:EventHub"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<EventHubTransportOptions>>().Value;
            return new EventHubProducerClient(opts.ConnectionString, opts.EventHubName);
        });

        builder.Services.TryAddSingleton<IOutboxTransport, EventHubOutboxTransport>();

        return builder;
    }

    public static IOutboxBuilder UseEventHub(
        this IOutboxBuilder builder,
        Func<IServiceProvider, EventHubProducerClient> clientFactory,
        Action<EventHubTransportOptions>? configure = null)
    {
        builder.Services.Configure<EventHubTransportOptions>(
            builder.Configuration.GetSection("Outbox:EventHub"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton(clientFactory);
        builder.Services.TryAddSingleton<IOutboxTransport, EventHubOutboxTransport>();

        return builder;
    }
}
