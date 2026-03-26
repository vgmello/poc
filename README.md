# Outbox library

A .NET outbox pattern library with pluggable transports (Kafka, EventHub) and stores (PostgreSQL, SQL Server). Guarantees at-least-once delivery with partition-based work distribution across multiple publisher instances.

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
dotnet add package Outbox.EventHub
```

Both store and transport packages pull in `Outbox.Core` automatically.

## Set up the publisher

### PostgreSQL + Kafka

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UsePostgreSql();
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
    outbox.UseSqlServer();
    outbox.UseEventHub();
});
```

## Configuration

Add to `appsettings.json`:

```json
{
    "Outbox": {
        "Publisher": {
            "BatchSize": 100,
            "MaxRetryCount": 5,
            "PublishThreadCount": 4
        },
        "PostgreSql": {
            "ConnectionString": "Host=localhost;Database=myapp;Username=postgres;Password=..."
        },
        "Kafka": {
            "BootstrapServers": "localhost:9092"
        }
    }
}
```

`PublishThreadCount` controls how many parallel threads process partitions within each publisher instance. The default of 4 works well for most workloads. Set to 1 for sequential processing. The benefit scales with partition key diversity—workloads concentrated on a single partition key won't see improvement since ordering requires sequential processing per key.

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

For SQL Server, use `"SqlServer"` instead of `"PostgreSql"`:

```json
{
    "Outbox": {
        "SqlServer": {
            "ConnectionString": "Server=localhost;Database=myapp;User Id=sa;Password=...",
            "SchemaName": "dbo",
            "CommandTimeoutSeconds": 30
        }
    }
}
```

See [docs/architecture.md](docs/architecture.md) for the full options reference.

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

This creates four tables (`outbox`, `outbox_dead_letter`, `outbox_publishers`, `outbox_partitions`), indexes, diagnostic views, and seeds 64 partitions.

## Write messages to the outbox

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

### Use headers

Store headers as a JSON dictionary in the `headers` column:

```sql
INSERT INTO outbox (topic_name, partition_key, event_type, headers, payload, event_datetime_utc)
VALUES ('orders', 'order-123', 'OrderCreated',
        '{"correlation-id": "abc-123", "source": "order-service"}',
        '{"id": "order-123"}'::bytea, clock_timestamp());
```

### Order multiple events in one transaction

Use `event_ordinal` to order events that share the same `event_datetime_utc`:

```sql
INSERT INTO outbox (topic_name, partition_key, event_type, payload, event_datetime_utc, event_ordinal)
VALUES
    ('orders', 'order-123', 'OrderCreated',  '...'::bytea, clock_timestamp(), 0),
    ('orders', 'order-123', 'OrderApproved', '...'::bytea, clock_timestamp(), 1);
```

## Event handlers

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

## Message interceptors

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

## Scale with multiple publishers

Run multiple instances to scale horizontally. Partitions are automatically distributed:

- 1 publisher — owns all 64 partitions
- 2 publishers — ~32 partitions each
- 4 publishers — ~16 partitions each

Each instance processes its partitions using `PublishThreadCount` parallel threads (default 4). When using publisher groups, each group gets its own independent thread count—configure it in `appsettings.json` or per-group in the `AddOutbox` registration.

Rebalancing happens automatically. Grace periods prevent dual processing during handover. See [docs/architecture.md](docs/architecture.md) for details.

## Publisher groups

Run multiple independent outbox pipelines in the same application. Each group gets its own outbox table, dead-letter table, partitions, health check, and configuration.

Use a table prefix per group so each group writes to separate tables in the same database. Config is group-scoped—each group's settings live under `Outbox:{GroupName}`:

```json
{
    "Outbox": {
        "Kafka": { "BootstrapServers": "localhost:9092" },
        "Orders": {
            "Publisher": { "PublishThreadCount": 8 },
            "PostgreSql": {
                "ConnectionString": "Host=localhost;Database=myapp;...",
                "TablePrefix": "orders_"
            }
        },
        "Notifications": {
            "Publisher": { "PublishThreadCount": 2 },
            "PostgreSql": {
                "ConnectionString": "Host=localhost;Database=myapp;...",
                "TablePrefix": "notifications_"
            }
        }
    }
}
```

```csharp
builder.Services.AddOutbox("orders", builder.Configuration, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});

builder.Services.AddOutbox("notifications", builder.Configuration, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});
```

This creates `orders_outbox`, `orders_outbox_dead_letter` for the orders group and `notifications_outbox`, `notifications_outbox_dead_letter` for notifications. Run the install script once per prefix, adjusting table names accordingly.

Each group runs its own `OutboxPublisherService` instance with independent partition ownership, circuit breakers, and health state. The shared `Outbox:Publisher`, `Outbox:PostgreSql`, and `Outbox:Kafka` config sections apply to all groups as a baseline—group-scoped sections override them.

Programmatic overrides via `Configure<T>("groupName", ...)` are still supported for advanced scenarios.

## Further reading

- [Architecture](docs/architecture.md) — How the publisher, ordering, and partitioning work
- [Production runbook](docs/production-runbook.md) — Monitoring, alerts, and incident response
- [Failure scenarios](docs/failure-scenarios-and-integration-tests.md) — All 14 tested failure modes
- [Known limitations](docs/known-limitations.md) — Transport-specific trade-offs
