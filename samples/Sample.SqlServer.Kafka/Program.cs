using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Outbox.Core.Builder;
using Outbox.Kafka;
using Outbox.Samples.Shared;
using Outbox.SqlServer;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UseSqlServer(async (sp, ct) =>
    {
        var connStr = builder.Configuration.GetConnectionString("OutboxDb")
            ?? throw new InvalidOperationException("ConnectionStrings:OutboxDb not set");
        var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        return conn;
    });
    outbox.UseKafka();
});

builder.Services.AddHostedService<SqlServerEventProducer>();
builder.Services.AddHostedService<KafkaConsumer>();

var host = builder.Build();
await host.RunAsync();
