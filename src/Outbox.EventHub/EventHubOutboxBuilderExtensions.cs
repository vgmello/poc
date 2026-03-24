// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;

namespace Outbox.EventHub;

public static class EventHubOutboxBuilderExtensions
{
    public static IEventHubOutboxBuilder UseEventHub(
        this IOutboxBuilder builder,
        Action<EventHubTransportOptions>? configure = null)
    {
        builder.Services.Configure<EventHubTransportOptions>(
            builder.Configuration.GetSection("Outbox:EventHub"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton<EventHubClientFactory>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<EventHubTransportOptions>>().Value;

            return eventHubName => new EventHubProducerClient(opts.ConnectionString, eventHubName);
        });
        builder.Services.TryAddSingleton<IOutboxTransport, EventHubOutboxTransport>();

        return new EventHubOutboxBuilder(builder);
    }
}

internal sealed class EventHubOutboxBuilder : IEventHubOutboxBuilder
{
    private readonly IOutboxBuilder _inner;

    public EventHubOutboxBuilder(IOutboxBuilder inner) => _inner = inner;

    public IServiceCollection Services => _inner.Services;
    public Microsoft.Extensions.Configuration.IConfiguration Configuration => _inner.Configuration;

    public IEventHubOutboxBuilder AddTransportInterceptor<TInterceptor>()
        where TInterceptor : class, ITransportMessageInterceptor<EventData>
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITransportMessageInterceptor<EventData>, TInterceptor>());

        return this;
    }

    /// <summary>
    ///     Registers a transport interceptor using a factory delegate.
    ///     Unlike the generic overload, calling this multiple times will register multiple instances.
    /// </summary>
    public IEventHubOutboxBuilder AddTransportInterceptor(
        Func<IServiceProvider, ITransportMessageInterceptor<EventData>> factory)
    {
        Services.AddSingleton(factory);

        return this;
    }

    public IEventHubOutboxBuilder UseClientFactory(EventHubClientFactory factory)
    {
        var existing = Services.FirstOrDefault(d => d.ServiceType == typeof(EventHubClientFactory));
        if (existing is not null) Services.Remove(existing);
        Services.AddSingleton(factory);

        return this;
    }

    // Delegate IOutboxBuilder methods — return this to preserve IEventHubOutboxBuilder for fluent chaining
    public IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure)
    {
        _inner.ConfigurePublisher(configure);

        return this;
    }

    public IOutboxBuilder ConfigureEvents<THandler>() where THandler : class, IOutboxEventHandler
    {
        _inner.ConfigureEvents<THandler>();

        return this;
    }

    public IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory)
    {
        _inner.ConfigureEvents(factory);

        return this;
    }

    public IOutboxBuilder AddMessageInterceptor<TInterceptor>() where TInterceptor : class, IOutboxMessageInterceptor
    {
        _inner.AddMessageInterceptor<TInterceptor>();

        return this;
    }

    public IOutboxBuilder AddMessageInterceptor(Func<IServiceProvider, IOutboxMessageInterceptor> factory)
    {
        _inner.AddMessageInterceptor(factory);

        return this;
    }
}
