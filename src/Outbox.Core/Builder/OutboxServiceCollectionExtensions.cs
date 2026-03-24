// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddHostedService<OutboxPublisherService>();

        services.AddHealthChecks()
            .AddCheck<OutboxHealthCheck>("outbox", tags: ["outbox", "ready"]);

        var builder = new OutboxBuilder(services, configuration);
        configure(builder);

        services.TryAddSingleton<IOutboxEventHandler, NoOpOutboxEventHandler>();

        return builder;
    }
}
