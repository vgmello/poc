# Outbox Test Harness Design

## Overview

A self-contained Dockerized test environment for the EventHub outbox pattern, adapted to use Redpanda (Kafka-compatible) instead of Azure EventHub. Consists of two .NET 10 console applications, Azure SQL Edge, and Redpanda, all running on ARM (linux/arm64).

## Project Structure

```
test/
├── docker-compose.yml
├── init-db/
│   └── init.sql                    # Outbox schema + seed OutboxPartitions
├── EventProducer/
│   ├── EventProducer.csproj
│   ├── Program.cs
│   ├── Dockerfile
│   └── appsettings.json
├── OutboxPublisher/
│   ├── OutboxPublisher.csproj
│   ├── Program.cs
│   ├── Dockerfile
│   └── appsettings.json
└── Shared/
    ├── Shared.csproj
    ├── OutboxRow.cs
    └── DbHelpers.cs
```

## Docker Infrastructure

### Services

| Service | Image | Purpose |
|---------|-------|---------|
| `sqlserver` | `mcr.microsoft.com/azure-sql-edge:latest` | ARM-compatible SQL Server, port 1433 |
| `redpanda` | `redpandadata/redpanda:latest` | Kafka-compatible broker, port 9092 |
| `sqlserver-init` | `mcr.microsoft.com/mssql-tools:latest` | Runs `init.sql` then exits |
| `event-producer` | Built from `EventProducer/Dockerfile` | Writes synthetic events to outbox |
| `outbox-publisher` | Built from `OutboxPublisher/Dockerfile` | Leases and publishes to Redpanda |

### Networking & Startup

- All services on a single bridge network (`outbox-net`), resolving by service name.
- Platform: `linux/arm64` on all services.
- SQL Edge healthcheck: `sqlcmd -S localhost -U sa -P <pwd> -Q "SELECT 1"`.
- Redpanda healthcheck: `curl -f http://localhost:9644/v1/status/ready`.
- `sqlserver-init` runs after `sqlserver` is healthy, executes `init.sql`, then exits.
- App services depend on `sqlserver-init` (completed) and `redpanda` (healthy).
- App services use `restart: on-failure`.

### Dockerfiles

Multi-stage builds:
- Build stage: `mcr.microsoft.com/dotnet/sdk:10.0`
- Runtime stage: `mcr.microsoft.com/dotnet/runtime:10.0`

## Shared Library

Minimal types to avoid duplication between the two apps:

- **`OutboxRow`** — Record matching the outbox table columns: `SequenceNumber`, `TopicName`, `PartitionKey`, `EventType`, `Headers`, `Payload`, `CreatedAtUtc`, `LeasedUntilUtc`, `LeaseOwner`, `RetryCount`.
- **`DbHelpers`** — Static helper for creating `SqlConnection` with basic transient retry. Contains the `INSERT INTO dbo.Outbox` helper used by EventProducer.

No abstractions or interfaces — plain data types and static methods.

## EventProducer (Event-Creating Service)

Simple console app that inserts synthetic events into the outbox table on a configurable timer.

### Behavior

- On startup, connects to Azure SQL Edge and waits until reachable.
- Runs a loop that every N seconds (default: 2s) inserts a batch of events into `dbo.Outbox`.
- Generates two event types: `OrderCreated` and `OrderUpdated`.
- Uses a pool of partition keys (`customer-1` through `customer-10`) to exercise partition routing.
- Payloads are simple JSON: `{"orderId": "<guid>", "amount": <random>, "timestamp": "<utc>"}`.
- Batch size and interval configurable via `appsettings.json`.
- Logs each insert batch to console (count, event types, partition keys).
- Graceful shutdown on SIGTERM/Ctrl+C via `CancellationToken`.

### Configuration

```json
{
  "ConnectionString": "Server=sqlserver;Database=OutboxTest;User Id=sa;Password=<pwd>;TrustServerCertificate=true",
  "TopicName": "orders",
  "BatchSize": 5,
  "IntervalSeconds": 2,
  "PartitionKeyCount": 10
}
```

## OutboxPublisher (Kafka/Redpanda Publisher)

New publisher class built on `Confluent.Kafka`, preserving the same outbox lease mechanism from the existing `OutboxPublisher.cs`.

### Core Loops

1. **PublishLoop** — Unified poll (lease fresh + expired rows), produce to Redpanda, delete on success.
2. **HeartbeatLoop** — Refresh `OutboxProducers` heartbeat every 10s.
3. **RebalanceLoop** — Claim/release partitions via fair-share every 30s.
4. **DeadLetterSweepLoop** — Move poison messages every 60s.
5. **OrphanSweepLoop** — Claim unowned partitions every 60s.

### Key Differences from EventHub Version

- Uses `IProducer<string, string>` from `Confluent.Kafka` instead of `EventHubProducerClient`.
- `PartitionKey` maps to the Kafka message key (Redpanda handles partition routing).
- `TopicName` maps directly to a Kafka topic.
- No manual batch size management — messages produced individually with `ProduceAsync`, grouped by partition key for ordering.
- `Headers` JSON is mapped to Kafka message headers.
- `EventType` added as a header (`event-type`).
- Circuit breaker logic retained — opens on consecutive produce failures per topic.
- Dead-letter and lease SQL logic is identical, reused from existing implementation.

### Configuration

```json
{
  "ConnectionString": "Server=sqlserver;Database=OutboxTest;User Id=sa;Password=<pwd>;TrustServerCertificate=true",
  "Kafka": {
    "BootstrapServers": "redpanda:9092"
  },
  "BatchSize": 100,
  "LeaseDurationSeconds": 45,
  "MaxRetryCount": 5
}
```

### Topic Auto-Creation

Redpanda auto-creates topics by default, so no pre-provisioning needed for testing.

## Database Schema

Reuses the existing outbox schema from `EventHubOutbox.sql`:

- `dbo.Outbox` — Primary event buffer
- `dbo.OutboxDeadLetter` — Poison message isolation
- `dbo.OutboxProducers` — Heartbeat registry
- `dbo.OutboxPartitions` — Partition affinity map (seeded with default partition count)
- `dbo.SequenceNumberList` — TVP for batch deletes
- All indexes from the existing schema

The `init.sql` script creates the database, runs the schema, and seeds `OutboxPartitions` with an initial partition count (e.g., 8 partitions).
