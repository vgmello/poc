using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Outbox.Core.Builder;
using Outbox.Kafka;
using Outbox.PostgreSQL;
using Outbox.Samples.Shared;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UsePostgreSql(async (sp, ct) =>
    {
        var connStr = builder.Configuration.GetConnectionString("OutboxDb")
            ?? throw new InvalidOperationException("ConnectionStrings:OutboxDb not set");
        var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        return conn;
    });
    outbox.UseKafka();
});

builder.Services.AddHostedService<PostgreSqlEventProducer>();
builder.Services.AddHostedService<KafkaConsumer>();

var host = builder.Build();
await host.RunAsync();
