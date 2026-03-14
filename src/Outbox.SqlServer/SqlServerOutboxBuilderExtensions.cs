using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.SqlServer;

public static class SqlServerOutboxBuilderExtensions
{
    public static IOutboxBuilder UseSqlServer(
        this IOutboxBuilder builder,
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        Action<SqlServerStoreOptions>? configure = null)
    {
        builder.Services.Configure<SqlServerStoreOptions>(
            builder.Configuration.GetSection("Outbox:SqlServer"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.AddSingleton(connectionFactory);

        builder.Services.TryAddSingleton<IOutboxStore, SqlServerOutboxStore>();
        builder.Services.TryAddSingleton<IDeadLetterManager, SqlServerDeadLetterManager>();

        return builder;
    }
}
