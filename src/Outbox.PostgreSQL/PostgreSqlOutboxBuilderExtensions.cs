// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;

namespace Outbox.PostgreSQL;

public static class PostgreSqlOutboxBuilderExtensions
{
    public static IOutboxBuilder UsePostgreSql(
        this IOutboxBuilder builder,
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        Action<PostgreSqlStoreOptions>? configure = null)
    {
        var groupName = builder.GroupName;

        if (groupName is not null)
        {
            builder.Services.AddOptions<PostgreSqlStoreOptions>(groupName)
                .BindConfiguration("Outbox:PostgreSql")
                .ValidateDataAnnotations()
                .ValidateOnStart();

            if (configure is not null)
                builder.Services.Configure(groupName, configure);

            builder.Services.Configure<PostgreSqlStoreOptions>(groupName, o => o.GroupName = groupName);

            builder.Services.AddKeyedSingleton(groupName, connectionFactory);
            builder.Services.TryAddKeyedSingleton<IOutboxStore>(groupName, (sp, key) =>
                new PostgreSqlOutboxStore(
                    sp.GetRequiredKeyedService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(key),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<PostgreSqlStoreOptions>>(),
                    sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>(),
                    groupName));
            builder.Services.TryAddKeyedSingleton<IDeadLetterManager>(groupName, (sp, key) =>
                new PostgreSqlDeadLetterManager(
                    sp.GetRequiredKeyedService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(key),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<PostgreSqlStoreOptions>>(),
                    groupName));
        }
        else
        {
            builder.Services.AddOptions<PostgreSqlStoreOptions>()
                .BindConfiguration("Outbox:PostgreSql")
                .ValidateDataAnnotations()
                .ValidateOnStart();

            if (configure is not null)
                builder.Services.Configure(configure);

            builder.Services.AddSingleton(connectionFactory);

            builder.Services.TryAddSingleton<IOutboxStore>(sp =>
                new PostgreSqlOutboxStore(
                    sp.GetRequiredService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<PostgreSqlStoreOptions>>(),
                    sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>()));
            builder.Services.TryAddSingleton<IDeadLetterManager>(sp =>
                new PostgreSqlDeadLetterManager(
                    sp.GetRequiredService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<PostgreSqlStoreOptions>>()));
        }

        return builder;
    }
}
