# Getting started

Wire up the outbox publisher in under 10 lines of code.

## Prerequisites

- .NET 10+
- A database: PostgreSQL or SQL Server
- A message broker: Kafka or Azure EventHub
- Database schema initialized (see [database setup](#database-setup))

## Install packages

Pick one store and one transport:

```
# Store (pick one)
dotnet add package Outbox.PostgreSQL
dotnet add package Outbox.SqlServer

# Transport (pick one)
dotnet add package Outbox.Kafka
dotbox add package Outbox.EventHub
```

Both store and transport packages pull in `Outbox.Core` automatically.

## Wire up the publisher

### PostgreSQL + Kafka

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UsePostgreSql(async (sp, ct) =>
    {
        var conn = new NpgsqlConnection(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("OutboxDb"));
        return conn;
    });
    outbox.UseKafka();
});

var app = builder.Build();
app.MapHealthChecks("/health/internal");
app.Run();
```

### SQL Server + EventHub

```csharp
builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UseSqlServer(async (sp, ct) =>
    {
        var conn = new SqlConnection(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("OutboxDb"));
        return conn;
    });
    outbox.UseEventHub();
});
```

## Configuration

Add to `appsettings.json`:

```json
{
    "ConnectionStrings": {
        "OutboxDb": "Host=localhost;Database=myapp;Username=postgres;Password=..."
    },
    "Outbox": {
        "Publisher": {
            "BatchSize": 100,
            "MaxRetryCount": 5,
            "LeaseDurationSeconds": 45
        },
        "Kafka": {
            "BootstrapServers": "localhost:9092"
        }
    }
}
```

For EventHub, replace the `Kafka` section:

```json
{
  "Outbox": {
    "EventHub": {
      "ConnectionString": "Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=..."
    }
  }
}
```

For SQL Server store, use `"SqlServer"` instead of `"PostgreSql"`:

```json
{
    "Outbox": {
        "SqlServer": {
            "SchemaName": "dbo",
            "CommandTimeoutSeconds": 30
        }
    }
}
```

See [architecture.md](architecture.md) for the full options reference.

## Database setup

Run the install script for your database. Both scripts are idempotent.

**PostgreSQL:**

```bash
psql -d myapp -f src/Outbox.PostgreSQL/db_scripts/install.sql
```

**SQL Server:**

```bash
sqlcmd -d myapp -i src/Outbox.SqlServer/db_scripts/install.sql
```

This creates four tables (`outbox`, `outbox_dead_letter`, `outbox_publishers`, `outbox_partitions`), indexes, diagnostic views, and seeds 32 partitions.

## Writing messages to the outbox

Insert messages into the `outbox` table within the same transaction as your business data:

```sql
-- PostgreSQL
INSERT INTO outbox (topic_name, partition_key, event_type, payload, event_datetime_utc)
VALUES ('orders', 'order-123', 'OrderCreated', '{"id": "order-123"}'::bytea, clock_timestamp());

-- SQL Server
INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Payload, EventDateTimeUtc)
VALUES ('orders', 'order-123', 'OrderCreated', CAST('{"id": "order-123"}' AS VARBINARY(MAX)), SYSUTCDATETIME());
```

The publisher picks up new rows automatically through its polling loop.

### Using headers

Store headers as a JSON dictionary in the `headers` column:

```sql
INSERT INTO outbox (topic_name, partition_key, event_type, headers, payload, event_datetime_utc)
VALUES ('orders', 'order-123', 'OrderCreated',
        '{"correlation-id": "abc-123", "source": "order-service"}',
        '{"id": "order-123"}'::bytea, clock_timestamp());
```

### Ordering multiple events in one transaction

Use `event_ordinal` to order events that share the same `event_datetime_utc`:

```sql
INSERT INTO outbox (topic_name, partition_key, event_type, payload, event_datetime_utc, event_ordinal)
VALUES
    ('orders', 'order-123', 'OrderCreated',  '...'::bytea, clock_timestamp(), 0),
    ('orders', 'order-123', 'OrderApproved', '...'::bytea, clock_timestamp(), 1);
```

## Event handler (optional)

Receive lifecycle callbacks:

```csharp
public class MyEventHandler : IOutboxEventHandler
{
    public Task OnMessagePublishedAsync(OutboxMessage message, CancellationToken ct)
    {
        Console.WriteLine($"Published {message.EventType} (seq {message.SequenceNumber})");
        return Task.CompletedTask;
    }

    public Task OnMessageDeadLetteredAsync(OutboxMessage message, CancellationToken ct)
    {
        Console.WriteLine($"Dead-lettered {message.EventType}: {message.SequenceNumber}");
        return Task.CompletedTask;
    }
}

// Register:
outbox.ConfigureEvents<MyEventHandler>();
```

## Message interceptors (optional)

Transform messages before they reach the broker:

```csharp
public class CompressionInterceptor : IOutboxMessageInterceptor
{
    public bool AppliesTo(OutboxMessage message) => message.Payload.Length > 1024;

    public ValueTask InterceptAsync(OutboxMessageContext context, CancellationToken ct)
    {
        context.Payload = Compress(context.Payload);
        context.PayloadContentType = "application/gzip";
        return ValueTask.CompletedTask;
    }
}

// Register:
outbox.AddMessageInterceptor<CompressionInterceptor>();
```

## Health checks

The publisher registers a health check named `"outbox"`. Map it to an endpoint:

```csharp
app.MapHealthChecks("/health/internal");
```

States: **Healthy** (all normal), **Degraded** (circuit breaker open or loops restarted), **Unhealthy** (publish loop down or heartbeat stale).

## Dead letter management

Inject `IDeadLetterManager` to manage poison messages:

```csharp
app.MapGet("/dead-letters", async (IDeadLetterManager dlm) =>
    await dlm.GetAsync(limit: 50, offset: 0, CancellationToken.None));

app.MapPost("/dead-letters/replay", async (IDeadLetterManager dlm, long[] ids) =>
    await dlm.ReplayAsync(ids, CancellationToken.None));
```

## Running multiple publishers

Scale horizontally by running multiple instances. Partitions are automatically distributed:

- 1 publisher → owns all 32 partitions
- 2 publishers → ~16 partitions each
- 4 publishers → ~8 partitions each

Rebalancing happens automatically. Grace periods prevent dual processing during handover. See [architecture.md](architecture.md) for details.

## Next steps

- [Architecture](architecture.md) — Deep dive into how the publisher, ordering, and partitioning work
- [Production runbook](production-runbook.md) — Monitoring, alerts, and incident response
- [Failure scenarios](failure-scenarios-and-integration-tests.md) — All 14 tested failure modes
- [Known limitations](known-limitations.md) — Transport-specific trade-offs
