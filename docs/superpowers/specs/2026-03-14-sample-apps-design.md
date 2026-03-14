# Sample Apps Design Spec

## Overview

4 sample console apps + shared Docker Compose infrastructure to validate all 4 combinations of the outbox library: (SqlServer, PostgreSQL) x (Kafka, EventHub). Each app demonstrates insert → poll → publish → consume round-trip.

## Infrastructure (docker-compose.yml)

| Service | Image | Ports |
|---------|-------|-------|
| `azuresqledge` | `mcr.microsoft.com/azure-sql-edge:latest` | 1433 |
| `postgres` | `postgres:17` | 5432 |
| `redpanda` | `redpanda/redpanda:v24.2.7` (or `docker.redpanda.com/redpandadata/redpanda`) | 9092 (Kafka), 8081 (Schema Registry), 8082 (Admin) |
| `eventhub-emulator` | `mcr.microsoft.com/azure-messaging/eventhubs-emulator:latest` | 5672 |
| `azurite` | `mcr.microsoft.com/azure-storage/azurite:latest` | 10000-10002 (required by EventHub emulator) |
| `dbinit` | Built from `samples/DbInit/Dockerfile` | — |

**Topic creation:** Redpanda topic `orders` with 8 partitions, created by `dbinit` service via `rpk`.

**EventHub config:** The emulator requires a `Config.json` defining namespace and event hub entities. We create one `orders` event hub with 8 partitions.

**DB initialization:** The `dbinit` service runs `install.sql` from both `Outbox.SqlServer` and `Outbox.PostgreSQL` packages, then seeds 8 partitions (instead of the default 32) to match our test setup.

## Sample App Structure

Each of the 4 apps is a .NET 10 console app (Worker Service template) with 3 services:

### Program.cs

```csharp
// Example: Sample.SqlServer.Kafka
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UseSqlServer(async (sp, ct) =>
    {
        var conn = new SqlConnection(builder.Configuration.GetConnectionString("OutboxDb"));
        await conn.OpenAsync(ct);
        return conn;
    });

    outbox.UseKafka();
});

builder.Services.AddHostedService<EventProducer>();
builder.Services.AddHostedService<KafkaConsumer>();

var host = builder.Build();
await host.RunAsync();
```

### EventProducer (shared logic, different DB)

Background service that inserts outbox rows every 2 seconds:
- Generates fake order events with random partition keys (`customer-1` through `customer-8`)
- Uses raw SQL INSERT (the library doesn't own the write side)
- Inserts a batch of 3-5 events per tick
- Event payload: `{ "orderId": "...", "customer": "...", "amount": ... }`
- Topic: `orders`
- Headers: `{ "source": "sample-app", "correlationId": "..." }`

### Consumer (transport-specific)

**KafkaConsumer:** Uses `Confluent.Kafka.IConsumer<string, string>` to consume from the `orders` topic. Logs each received message with key, headers, and payload.

**EventHubConsumer:** Uses `Azure.Messaging.EventHubs.Consumer.EventHubConsumerClient` to consume from the `orders` event hub. Logs each received event with partition key, properties, and body.

### appsettings.json

```json
{
  "ConnectionStrings": {
    "OutboxDb": "Server=azuresqledge;Database=OutboxSample;User Id=sa;Password=...;TrustServerCertificate=True"
  },
  "Outbox": {
    "Publisher": {
      "BatchSize": 10,
      "MinPollIntervalMs": 200,
      "MaxPollIntervalMs": 2000
    },
    "Kafka": {
      "BootstrapServers": "redpanda:9092"
    }
  }
}
```

## File Structure

```
samples/
├── docker-compose.yml
├── DbInit/
│   ├── Dockerfile
│   ├── init-sqlserver.sh
│   ├── init-postgres.sh
│   └── init-redpanda.sh
├── eventhub-emulator-config/
│   └── Config.json
├── Shared/
│   ├── EventProducerBase.cs      # Shared timer logic, abstract InsertOutboxRowAsync
│   └── SampleEvent.cs            # Event payload model
├── Sample.SqlServer.Kafka/
│   ├── Dockerfile
│   ├── Sample.SqlServer.Kafka.csproj
│   ├── Program.cs
│   ├── SqlServerEventProducer.cs
│   ├── KafkaConsumer.cs
│   └── appsettings.json
├── Sample.SqlServer.EventHub/
│   ├── Dockerfile
│   ├── Sample.SqlServer.EventHub.csproj
│   ├── Program.cs
│   ├── SqlServerEventProducer.cs
│   ├── EventHubConsumer.cs
│   └── appsettings.json
├── Sample.PostgreSQL.Kafka/
│   ├── Dockerfile
│   ├── Sample.PostgreSQL.Kafka.csproj
│   ├── Program.cs
│   ├── PostgreSqlEventProducer.cs
│   ├── KafkaConsumer.cs
│   └── appsettings.json
└── Sample.PostgreSQL.EventHub/
    ├── Dockerfile
    ├── Sample.PostgreSQL.EventHub.csproj
    ├── Program.cs
    ├── PostgreSqlEventProducer.cs
    ├── EventHubConsumer.cs
    └── appsettings.json
```

## Docker Compose Orchestration

**Startup order:**
1. `azuresqledge`, `postgres`, `redpanda`, `azurite` start first
2. `eventhub-emulator` depends on `azurite`
3. `dbinit` depends on all infra services — waits for readiness, runs schema + topic creation
4. All 4 sample apps depend on `dbinit`

**Health checks:**
- Azure SQL Edge: `SELECT 1` via `/opt/mssql-tools/bin/sqlcmd`
- PostgreSQL: `pg_isready`
- Redpanda: `rpk cluster health`
- EventHub Emulator: TCP check on 5672

## Validation Criteria

Each sample app should log:
1. `EventProducer: Inserted {count} events for {partitionKey}` — proving writes work
2. `OutboxPublisher: registered as {producerId}` — library starts
3. `Consumer: Received event {orderId} from {topic}/{partitionKey}` — messages arrive at broker

A successful run means all 3 log lines appear for each of the 4 apps.
