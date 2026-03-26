// Copyright (c) OrgName. All rights reserved.

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Engine;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

public static class OutboxServiceCollectionExtensions
{
    public static IOutboxBuilder AddOutbox(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IOutboxBuilder> configure)
    {
        services.AddOptions<OutboxPublisherOptions>()
            .BindConfiguration("Outbox:Publisher")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<OutboxInstrumentation>();
        services.AddSingleton<OutboxHealthState>();

        services.AddHostedService(sp => new OutboxPublisherService(sp,
            sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>(),
            sp.GetRequiredService<IHostApplicationLifetime>()));

        services.AddHealthChecks()
            .AddCheck<OutboxHealthCheck>("outbox", tags: ["outbox", "ready"]);

        var builder = new OutboxBuilder(services, configuration);
        configure(builder);

        services.TryAddSingleton<IOutboxEventHandler, NoOpOutboxEventHandler>();

        return builder;
    }

    public static IOutboxBuilder AddOutbox(
        this IServiceCollection services,
        string groupName,
        IConfiguration configuration,
        Action<IOutboxBuilder> configure)
    {
        services.AddOptions<OutboxPublisherOptions>(groupName)
            .BindConfiguration("Outbox:Publisher")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.Configure<OutboxPublisherOptions>(groupName, o => o.GroupName = groupName);

        services.AddLogging();
        services.AddMetrics();

        services.AddKeyedSingleton<OutboxInstrumentation>(groupName,
            (sp, _) => new OutboxInstrumentation(sp.GetRequiredService<IMeterFactory>(), groupName));
        services.AddKeyedSingleton<OutboxHealthState>(groupName, (_, _) => new OutboxHealthState());

        services.AddHostedService(sp => new OutboxPublisherService(sp,
            sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            groupName));

        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                $"outbox-{groupName}",
                sp => new OutboxHealthCheck(
                    sp.GetRequiredKeyedService<OutboxHealthState>(groupName),
                    sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>(),
                    groupName),
                failureStatus: null,
                tags: ["outbox", groupName, "ready"]));

        var builder = new OutboxBuilder(services, configuration, groupName);
        configure(builder);

        services.TryAddKeyedSingleton<IOutboxEventHandler>(groupName,
            (_, _) => new NoOpOutboxEventHandler());

        return builder;
    }
}
