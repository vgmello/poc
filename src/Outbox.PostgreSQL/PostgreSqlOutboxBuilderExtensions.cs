// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.PostgreSQL;

public static class PostgreSqlOutboxBuilderExtensions
{
    public static IOutboxBuilder UsePostgreSql(
        this IOutboxBuilder builder,
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        Action<PostgreSqlStoreOptions>? configure = null)
    {
        builder.Services.AddOptions<PostgreSqlStoreOptions>()
            .BindConfiguration("Outbox:PostgreSql")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.AddSingleton(connectionFactory);

        builder.Services.TryAddSingleton<IOutboxStore, PostgreSqlOutboxStore>();
        builder.Services.TryAddSingleton<IDeadLetterManager, PostgreSqlDeadLetterManager>();

        return builder;
    }
}
