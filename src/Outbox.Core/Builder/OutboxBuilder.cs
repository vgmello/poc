// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

internal sealed class OutboxBuilder : IOutboxBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }

    public OutboxBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }

    public IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure)
    {
        Services.Configure(configure);

        return this;
    }

    public IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler
    {
        Services.AddSingleton<IOutboxEventHandler, THandler>();

        return this;
    }

    public IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory)
    {
        Services.AddSingleton(factory);

        return this;
    }

    public IOutboxBuilder AddMessageInterceptor<TInterceptor>()
        where TInterceptor : class, IOutboxMessageInterceptor
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOutboxMessageInterceptor, TInterceptor>());

        return this;
    }

    /// <inheritdoc />
    public IOutboxBuilder AddMessageInterceptor(Func<IServiceProvider, IOutboxMessageInterceptor> factory)
    {
        Services.AddSingleton<IOutboxMessageInterceptor>(factory);

        return this;
    }
}
