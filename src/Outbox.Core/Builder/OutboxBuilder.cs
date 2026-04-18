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
    public string? GroupName { get; }

    public OutboxBuilder(IServiceCollection services, IConfiguration configuration, string? groupName = null)
    {
        Services = services;
        Configuration = configuration;
        GroupName = groupName;
    }

    public IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure)
    {
        if (GroupName is not null)
            Services.Configure(GroupName, configure);
        else
            Services.Configure(configure);

        return this;
    }

    public IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler
    {
        if (GroupName is not null)
            Services.AddKeyedSingleton<IOutboxEventHandler, THandler>(GroupName);
        else
            Services.AddSingleton<IOutboxEventHandler, THandler>();

        return this;
    }

    public IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory)
    {
        if (GroupName is not null)
            Services.AddKeyedSingleton<IOutboxEventHandler>(GroupName, (sp, _) => factory(sp));
        else
            Services.AddSingleton(factory);

        return this;
    }

    public IOutboxBuilder AddMessageInterceptor<TInterceptor>()
        where TInterceptor : class, IOutboxMessageInterceptor
    {
        if (GroupName is not null)
        {
            // No framework TryAddKeyedEnumerable exists, so deduplicate manually
            // to match the unnamed-group TryAddEnumerable behavior.
            var alreadyRegistered = Services.Any(d =>
                d.IsKeyedService
                && d.ServiceType == typeof(IOutboxMessageInterceptor)
                && Equals(d.ServiceKey, GroupName)
                && d.KeyedImplementationType == typeof(TInterceptor));

            if (!alreadyRegistered)
                Services.AddKeyedSingleton<IOutboxMessageInterceptor, TInterceptor>(GroupName);
        }
        else
        {
            Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IOutboxMessageInterceptor, TInterceptor>());
        }

        return this;
    }

    /// <inheritdoc />
    public IOutboxBuilder AddMessageInterceptor(Func<IServiceProvider, IOutboxMessageInterceptor> factory)
    {
        if (GroupName is not null)
            Services.AddKeyedSingleton<IOutboxMessageInterceptor>(GroupName, (sp, _) => factory(sp));
        else
            Services.AddSingleton<IOutboxMessageInterceptor>(factory);

        return this;
    }
}
