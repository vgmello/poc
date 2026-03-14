# Outbox Library Design Spec

## Overview

A .NET 10 opinionated library that encapsulates the transactional outbox pattern for reliable message publishing. The library handles the **read/publish side** — polling the outbox table, sending to a pluggable transport, and managing dead letters. The write side (inserting into the outbox table) is the developer's responsibility.

**Target:** Open-source NuGet package. Opinionated but extensible.

## Package Structure

```
Outbox.Core              → Microsoft.Extensions.* only
├── Outbox.SqlServer     → Core + Microsoft.Data.SqlClient
├── Outbox.PostgreSQL    → Core + Npgsql
├── Outbox.Kafka         → Core + Confluent.Kafka
└── Outbox.EventHub      → Core + Azure.Messaging.EventHubs
```

One NuGet package per transport, one per database provider. Target framework: .NET 10.

### Package Contents

**Outbox.Core:**
- All abstractions: `IOutboxTransport`, `IOutboxStore`, `IOutboxEventHandler`, `IDeadLetterManager`
- Publisher engine: `OutboxPublisher` as `BackgroundService`
- Builder: `IOutboxBuilder` / `OutboxBuilder`
- Options: `OutboxPublisherOptions`
- Observability: `ActivitySource` + `Meter` definitions
- Models: `OutboxMessage`, `DeadLetteredMessage`

**Provider packages (SqlServer, PostgreSQL, Kafka, EventHub):**
- Implementation of the relevant core interface (`IOutboxStore` or `IOutboxTransport`)
- Provider-specific options class
- Extension methods on `IOutboxBuilder`
- `db_scripts/` folder with `install.sql` (database packages only)

Database installation/migrations are **not** handled by the library. Each database package ships a `db_scripts/` folder containing `sqlserver/install.sql` or `pgsql/install.sql` for the user to integrate into their own migration tooling.

## Core Abstractions

### IOutboxTransport

```csharp
public interface IOutboxTransport : IAsyncDisposable
{
    Task SendAsync(
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken);
}
```

Messages arrive pre-grouped by `(TopicName, PartitionKey)` by the publisher engine. The transport sends them. On any failure, it throws — the publisher handles retry/release logic.

### IOutboxStore

```csharp
public interface IOutboxStore
{
    Task<string> RegisterProducerAsync(CancellationToken ct);
    Task UnregisterProducerAsync(string producerId, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        string producerId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct);

    Task DeletePublishedAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task ReleaseLeaseAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    // Partition management
    Task HeartbeatAsync(string producerId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string producerId, CancellationToken ct);
    Task RebalanceAsync(string producerId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string producerId, CancellationToken ct);

    // Dead letter sweep (background safety net)
    Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct);
}
```

### IOutboxEventHandler

```csharp
public interface IOutboxEventHandler
{
    Task OnMessagePublishedAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnMessageDeadLetteredAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnCircuitBreakerStateChangedAsync(
        string topicName, CircuitState state, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnRebalanceAsync(
        string producerId, IReadOnlyList<int> ownedPartitions, CancellationToken ct) =>
        Task.CompletedTask;
}

public enum CircuitState { Closed, Open, HalfOpen }
```

All methods have default implementations (no-op). Users override only what they care about.

### IDeadLetterManager

```csharp
public interface IDeadLetterManager
{
    Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct);

    Task ReplayAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task PurgeAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task PurgeAllAsync(CancellationToken ct);
}
```

`ReplayAsync` is atomic — it moves rows back to the outbox table in a single transaction with `RetryCount = 0`, re-entering the normal publish pipeline.

### Models

```csharp
public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    DateTime EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount);

public sealed record DeadLetteredMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    DateTime EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount,
    DateTime DeadLetteredAtUtc);
```

## Options & Configuration

### OutboxPublisherOptions (Core)

```csharp
public sealed class OutboxPublisherOptions
{
    public int BatchSize { get; set; } = 100;
    public int LeaseDurationSeconds { get; set; } = 45;
    public int MaxRetryCount { get; set; } = 5;
    public int MinPollIntervalMs { get; set; } = 100;
    public int MaxPollIntervalMs { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 10_000;
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int PartitionGracePeriodSeconds { get; set; } = 60;
    public int RebalanceIntervalMs { get; set; } = 30_000;
    public int OrphanSweepIntervalMs { get; set; } = 60_000;
    public int DeadLetterSweepIntervalMs { get; set; } = 60_000;
    public int CircuitBreakerFailureThreshold { get; set; } = 3;
    public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;
}
```

Bound from `Outbox:Publisher` in appsettings.json. Mutable via `services.Configure<OutboxPublisherOptions>()`.

### KafkaTransportOptions

```csharp
public sealed class KafkaTransportOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string Acks { get; set; } = "All";
    public bool EnableIdempotence { get; set; } = true;
    public int MessageSendMaxRetries { get; set; } = 3;
    public int RetryBackoffMs { get; set; } = 500;
    public int LingerMs { get; set; } = 5;
    public int MessageTimeoutMs { get; set; } = 15_000;
}
```

Bound from `Outbox:Kafka`.

### EventHubTransportOptions

```csharp
public sealed class EventHubTransportOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string EventHubName { get; set; } = string.Empty;
    public int MaxBatchSizeBytes { get; set; } = 1_048_576; // 1MB
}
```

Bound from `Outbox:EventHub`.

### Database Store Options

```csharp
public sealed class SqlServerStoreOptions
{
    public int CommandTimeoutSeconds { get; set; } = 30;
    public string SchemaName { get; set; } = "dbo";
}

public sealed class PostgreSqlStoreOptions
{
    public int CommandTimeoutSeconds { get; set; } = 30;
    public string SchemaName { get; set; } = "public";
}
```

Bound from `Outbox:SqlServer` / `Outbox:PostgreSql`.

### appsettings.json Example

```json
{
  "Outbox": {
    "Publisher": {
      "BatchSize": 100,
      "LeaseDurationSeconds": 45,
      "MaxRetryCount": 5
    },
    "Kafka": {
      "BootstrapServers": "localhost:9092",
      "Acks": "All"
    },
    "SqlServer": {
      "CommandTimeoutSeconds": 30,
      "SchemaName": "dbo"
    }
  }
}
```

## Builder API & Registration

### IOutboxBuilder

```csharp
public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
    IConfiguration Configuration { get; }

    IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure);
    IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler;
    IOutboxBuilder ConfigureEvents(Action<OutboxEventHandlerOptions> configure);
}
```

### Entry Point

```csharp
public static class OutboxServiceCollectionExtensions
{
    public static IOutboxBuilder AddOutbox(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IOutboxBuilder> configure)
    {
        // 1. Bind OutboxPublisherOptions from configuration
        // 2. Register OutboxPublisher as hosted service
        // 3. Register ActivitySource + Meter
        // 4. Invoke user's configure action
        // 5. Validate: at least one transport + one store registered
    }
}
```

### Database Provider Extensions

```csharp
// In Outbox.SqlServer
public static class SqlServerOutboxBuilderExtensions
{
    public static IOutboxBuilder UseSqlServer(
        this IOutboxBuilder builder,
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        Action<SqlServerStoreOptions>? configure = null)
    {
        // 1. TryAddSingleton: SqlServerOutboxStore as IOutboxStore
        // 2. TryAddSingleton: SqlServerDeadLetterManager as IDeadLetterManager
        // 3. Bind SqlServerStoreOptions from builder.Configuration
        // 4. Apply user's configure action if provided
        // 5. Store the connection factory
    }
}

// In Outbox.PostgreSQL — same pattern
public static class PostgreSqlOutboxBuilderExtensions
{
    public static IOutboxBuilder UsePostgreSql(
        this IOutboxBuilder builder,
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        Action<PostgreSqlStoreOptions>? configure = null);
}
```

The connection factory signature `Func<IServiceProvider, CancellationToken, Task<DbConnection>>` gives the user full control. They can resolve keyed services, use `NpgsqlDataSource`, Azure managed identity tokens, or anything else. The library never owns connection lifecycle.

### Transport Extensions

```csharp
// In Outbox.Kafka
public static class KafkaOutboxBuilderExtensions
{
    // Library creates producer from options
    public static IOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Action<KafkaTransportOptions>? configure = null);

    // User provides custom producer factory
    public static IOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Func<IServiceProvider, IProducer<string, string>> producerFactory,
        Action<KafkaTransportOptions>? configure = null);
}

// In Outbox.EventHub — same pattern
public static class EventHubOutboxBuilderExtensions
{
    public static IOutboxBuilder UseEventHub(
        this IOutboxBuilder builder,
        Action<EventHubTransportOptions>? configure = null);

    public static IOutboxBuilder UseEventHub(
        this IOutboxBuilder builder,
        Func<IServiceProvider, EventHubProducerClient> clientFactory,
        Action<EventHubTransportOptions>? configure = null);
}
```

All transport extensions use `TryAddSingleton` for `IOutboxTransport`, allowing user overrides.

### Full Registration Example

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UseSqlServer(
        connectionFactory: async (sp, ct) =>
        {
            var conn = new SqlConnection(
                builder.Configuration.GetConnectionString("OutboxDb"));
            await conn.OpenAsync(ct);
            return conn;
        },
        configure: o => o.SchemaName = "messaging"
    );

    outbox.UseKafka(kafka =>
    {
        kafka.BootstrapServers = "kafka:9092";
    });

    outbox.ConfigurePublisher(pub =>
    {
        pub.BatchSize = 200;
    });

    outbox.ConfigureEvents<MyOutboxEventHandler>();
});
```

### Startup Validation

When the host starts, `OutboxPublisher` validates:
- At least one `IOutboxStore` is registered
- At least one `IOutboxTransport` is registered
- `OutboxPublisherOptions` passes sanity checks (e.g., `LeaseDurationSeconds` is large enough relative to transport timeout)

Fails fast with `InvalidOperationException` and a clear message.

## Publisher Engine

### Architecture

`OutboxPublisher` is an `internal sealed class` extending `BackgroundService`, registered by `AddOutbox`.

```csharp
internal sealed class OutboxPublisher : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;
    private readonly ILogger<OutboxPublisher> _logger;
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
}
```

Uses `IOptionsMonitor<OutboxPublisherOptions>` (not `IOptions`) so runtime changes via appsettings.json reload are picked up without restarting the host. The publisher reads `_options.CurrentValue` on each loop iteration.

### Loop Orchestration

```
ExecuteAsync(ct)
├── RegisterProducer
├── Task.WhenAll(
│   ├── PublishLoopAsync(ct)
│   ├── HeartbeatLoopAsync(ct)
│   ├── RebalanceLoopAsync(ct)
│   ├── OrphanSweepLoopAsync(ct)
│   └── DeadLetterSweepLoopAsync(ct)
│)
└── UnregisterProducer (on cancellation)
```

### Publish Loop Flow

```
1. LeaseBatchAsync() via IOutboxStore
2. Group by (TopicName, PartitionKey)
3. For each group:
   a. Check circuit breaker
   b. If open → ReleaseLeaseAsync(), fire OnCircuitBreakerStateChanged, continue
   c. SendAsync() via IOutboxTransport
   d. On success → DeletePublishedAsync(), fire OnMessagePublished
   e. On failure → increment circuit breaker, ReleaseLeaseAsync()
4. If poison detected (RetryCount >= MaxRetryCount):
   a. DeadLetterAsync() via IOutboxStore
   b. Fire OnMessageDeadLettered
5. Adaptive poll interval (backoff on empty, reset on non-empty)
```

### Observability

Each loop operation is wrapped with:
- **Tracing:** `ActivitySource.StartActivity("Outbox.PublishBatch")` with tags for batch size, topic, partition key, producer ID
- **Metrics:** Counters for `outbox.messages.published`, `outbox.messages.dead_lettered`, `outbox.circuit_breaker.state_changes`; histograms for `outbox.publish.duration`, `outbox.poll.duration`
- **Logging:** Structured `ILogger` — Debug for poll cycles, Information for rebalance/dead-letter, Warning for circuit breaker, Error for failures

## Dead Letter Manager

Implemented by each database provider package. Registered automatically by `.UseSqlServer()` / `.UsePostgreSql()` via `TryAddSingleton`.

```csharp
internal sealed class SqlServerDeadLetterManager : IDeadLetterManager
{
    // GetAsync: SELECT from OutboxDeadLetter with OFFSET/FETCH pagination
    // ReplayAsync: Atomic DELETE from OutboxDeadLetter + INSERT into Outbox (RetryCount = 0)
    // PurgeAsync: DELETE from OutboxDeadLetter WHERE SequenceNumber IN (...)
    // PurgeAllAsync: TRUNCATE/DELETE from OutboxDeadLetter
}
```

Users inject `IDeadLetterManager` wherever needed (admin API, background job, CLI):

```csharp
app.MapGet("/outbox/dead-letters", async (
    IDeadLetterManager dlm, int limit = 50, int offset = 0, CancellationToken ct = default) =>
    Results.Ok(await dlm.GetAsync(limit, offset, ct)));

app.MapPost("/outbox/dead-letters/replay", async (
    IDeadLetterManager dlm, long[] sequenceNumbers, CancellationToken ct = default) =>
{
    await dlm.ReplayAsync(sequenceNumbers, ct);
    return Results.NoContent();
});
```

## Invariants & Guarantees

These are non-negotiable. Every `IOutboxStore` and `IOutboxTransport` implementation must uphold them.

### 1. At-Least-Once Delivery
- Messages are only deleted from the outbox **after** successful transport acknowledgment
- If the publisher crashes between send and delete, the lease expires and the message is re-delivered
- The library never silently drops messages — failure paths either retry or dead-letter

### 2. Per-PartitionKey Causal Ordering
- `LeaseBatchAsync` must return rows ordered by `(EventDateTimeUtc, EventOrdinal)`, not by `SequenceNumber`
- The publisher groups by `(TopicName, PartitionKey)` and sends each group sequentially
- **All-or-nothing per group:** if any message in a `(TopicName, PartitionKey)` group fails, none are deleted — the entire group returns to the pool preserving order
- Partition affinity via `IOutboxStore` ensures only one publisher processes a given partition at a time

### 3. No Data Loss on Crash
- Lease-based work distribution: no external coordinator, no distributed locks
- `LeaseOwner` guard on delete prevents zombie publishers from deleting rows rebalanced to another publisher
- Producer unregistration and partition release on graceful shutdown
- Heartbeat-based stale detection with grace period for recovery

### 4. Poison Message Isolation
- Messages exceeding `MaxRetryCount` are moved to the dead letter table, never retried indefinitely
- Inline detection during publish loop + background sweep as safety net (belt-and-suspenders)
- Dead-lettering is atomic (delete from outbox + insert into dead letter in one transaction)

### 5. Circuit Breaker Preserves Retry Semantics
- When a circuit opens, leased rows are **released** (lease cleared), not abandoned
- `RetryCount` is **not incremented** on circuit breaker release — the failure is transient, not message-specific
- Prevents healthy messages from being dead-lettered due to transport outages

### 6. Store Implementation Contract

Any `IOutboxStore` implementation must guarantee:
- `LeaseBatchAsync` uses row-level locking with skip-locked semantics
- `DeletePublishedAsync` includes a `LeaseOwner` guard
- `DeadLetterAsync` is atomic (single transaction)
- `RebalanceAsync` respects fair-share calculation and grace periods
- Ordering is always `(EventDateTimeUtc, EventOrdinal)`

### 7. Transport Implementation Contract

Any `IOutboxTransport` implementation must guarantee:
- `SendAsync` throws on any failure — the publisher handles retry/release
- Messages are sent with their `PartitionKey` for broker partition affinity
- Headers from `OutboxMessage.Headers` (JSON) are deserialized and forwarded to the broker's native header mechanism
