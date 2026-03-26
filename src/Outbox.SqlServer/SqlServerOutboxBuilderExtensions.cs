// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;

namespace Outbox.SqlServer;

public static class SqlServerOutboxBuilderExtensions
{
    public static IOutboxBuilder UseSqlServer(
        this IOutboxBuilder builder,
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        Action<SqlServerStoreOptions>? configure = null)
    {
        var groupName = builder.GroupName;

        if (groupName is not null)
        {
            builder.Services.AddOptions<SqlServerStoreOptions>(groupName)
                .BindConfiguration("Outbox:SqlServer")
                .ValidateDataAnnotations()
                .ValidateOnStart();

            if (configure is not null)
                builder.Services.Configure(groupName, configure);

            builder.Services.Configure<SqlServerStoreOptions>(groupName, o => o.GroupName = groupName);

            builder.Services.AddKeyedSingleton(groupName, connectionFactory);
            builder.Services.TryAddKeyedSingleton<IOutboxStore>(groupName, (sp, key) =>
                new SqlServerOutboxStore(
                    sp.GetRequiredKeyedService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(key),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<SqlServerStoreOptions>>(),
                    sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>(),
                    groupName));
            builder.Services.TryAddKeyedSingleton<IDeadLetterManager>(groupName, (sp, key) =>
                new SqlServerDeadLetterManager(
                    sp.GetRequiredKeyedService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(key),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<SqlServerStoreOptions>>(),
                    groupName));
        }
        else
        {
            builder.Services.AddOptions<SqlServerStoreOptions>()
                .BindConfiguration("Outbox:SqlServer")
                .ValidateDataAnnotations()
                .ValidateOnStart();

            if (configure is not null)
                builder.Services.Configure(configure);

            builder.Services.AddSingleton(connectionFactory);

            builder.Services.TryAddSingleton<IOutboxStore>(sp =>
                new SqlServerOutboxStore(
                    sp.GetRequiredService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<SqlServerStoreOptions>>(),
                    sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>()));
            builder.Services.TryAddSingleton<IDeadLetterManager>(sp =>
                new SqlServerDeadLetterManager(
                    sp.GetRequiredService<Func<IServiceProvider, CancellationToken, Task<DbConnection>>>(),
                    sp,
                    sp.GetRequiredService<IOptionsMonitor<SqlServerStoreOptions>>()));
        }

        return builder;
    }
}
