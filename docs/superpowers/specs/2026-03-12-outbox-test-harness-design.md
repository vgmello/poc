# Outbox Test Harness Design

## Overview

A self-contained Dockerized test environment for the EventHub outbox pattern, adapted to use Redpanda (Kafka-compatible) instead of Azure EventHub. Consists of two .NET 10 console applications, Azure SQL Edge, and Redpanda, all running on ARM (linux/arm64).

## Project Structure

```
test/
├── docker-compose.yml
├── .env                            # SA_PASSWORD and shared config
├── init-db/
│   └── init.sql                    # Outbox schema + seed OutboxPartitions
├── DbInit/
│   ├── DbInit.csproj
│   ├── Program.cs
│   └── Dockerfile
├── EventProducer/
│   ├── EventProducer.csproj
│   ├── Program.cs
│   ├── Dockerfile
│   └── appsettings.json
├── OutboxPublisher/
│   ├── OutboxPublisher.csproj
│   ├── Program.cs
│   ├── KafkaOutboxPublisher.cs
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
| `redpanda` | `redpandadata/redpanda:v24.2.7` | Kafka-compatible broker, port 9092 |
| `sqlserver-init` | Built from `DbInit/Dockerfile` | .NET console app that runs `init.sql` batches via `SqlConnection`, then exits |
| `event-producer` | Built from `EventProducer/Dockerfile` | Writes synthetic events to outbox |
| `outbox-publisher` | Built from `OutboxPublisher/Dockerfile` | Leases and publishes to Redpanda |

### Networking & Startup

- All services on a single bridge network (`outbox-net`), resolving by service name.
- Platform: `linux/arm64` on all services.
- SQL Edge healthcheck: Python TCP socket check (`python3 -c "import socket; s=socket.create_connection(('localhost',1433), timeout=2); s.close()"`). Note: Azure SQL Edge ARM64 does not include `sqlcmd`.
- Redpanda healthcheck: `curl -f http://localhost:9644/v1/status/ready`.
- `sqlserver-init` is a .NET 10 console app (`DbInit`) that reads `init.sql`, splits on `GO` batch separators via regex, and executes each batch via `SqlConnection`. The first batch (CREATE DATABASE) runs against `master`; remaining batches run against `OutboxTest`. Runs after `sqlserver` is healthy via `depends_on: sqlserver: condition: service_healthy`, then exits.
- App services depend on `sqlserver-init` (`condition: service_completed_successfully`) and `redpanda` (`condition: service_healthy`).
- App services use `restart: on-failure`.
- Password is injected via `SA_PASSWORD` environment variable from `.env` file (not hardcoded in config).

### Dockerfiles

Multi-stage builds:
- Build stage: `mcr.microsoft.com/dotnet/sdk:10.0`
- Runtime stage: `mcr.microsoft.com/dotnet/runtime:10.0`

## Shared Library

Minimal types to avoid duplication between the two apps:

- **`OutboxRow`** — Record matching the columns returned by the lease query: `SequenceNumber`, `TopicName`, `PartitionKey`, `EventType`, `Headers`, `Payload`, `RetryCount`. Does not include `LeasedUntilUtc` or `LeaseOwner` since the publisher does not need them after acquiring the lease (matches the existing C# implementation).
- **`DbHelpers`** — Static helper for creating `SqlConnection` with basic transient retry. Contains `InsertOutboxRowAsync(SqlConnection, string topicName, string partitionKey, string eventType, string? headers, string payload)` used by EventProducer.

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

Connection string uses `SA_PASSWORD` from environment variable:

```json
{
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
- Messages are produced individually with `ProduceAsync`, grouped by partition key for ordering. All `ProduceAsync` calls for a `(TopicName, PartitionKey)` group are awaited and tracked. If any produce call fails, the entire group is not deleted from the outbox — preserving the all-or-nothing ordering invariant from the EventHub version. No Kafka transactions are needed; the invariant is maintained at the SQL delete layer, not the Kafka produce layer (same approach as EventHub).
- `Headers` JSON is deserialized and mapped to Kafka message headers. Header values are UTF-8 encoded as `byte[]` via `Encoding.UTF8.GetBytes` (Kafka headers are `byte[]`, not strings). `EventType` is added as an `event-type` header alongside deserialized headers.
- Circuit breaker logic retained — opens on consecutive produce failures per topic.
- Dead-letter and lease SQL logic is identical, reused from existing implementation.

### Configuration

Connection string uses `SA_PASSWORD` from environment variable:

```json
{
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
- `dbo.OutboxPartitions` — Partition affinity map (publisher work-distribution buckets)
- `dbo.SequenceNumberList` — TVP for batch deletes
- All indexes from the existing schema

The `init.sql` script creates the database, runs the schema, and seeds `OutboxPartitions` with 8 buckets. This count controls how work is distributed across publisher instances and is independent of Redpanda's topic partition count (which the broker manages separately).

## Verification

End-to-end verification is done by observing console logs from both apps and optionally consuming from Redpanda using `rpk topic consume orders` from the Redpanda container. A dedicated consumer component is out of scope for this test harness.
