# How the outbox works

This document explains the outbox pattern library from the ground up—how messages flow, how ordering is maintained, how partitions are owned, and how failures are handled.

## The outbox pattern

Your application writes a business event to an `outbox` table inside the same database transaction as the business data change. A background publisher then picks up those events and sends them to a message broker (Kafka or EventHub). This guarantees at-least-once delivery: if the transaction commits, the message will eventually reach the broker.

```
┌─────────────────────────────────────┐
│  Application transaction            │
│  ├── UPDATE orders SET status = ... │
│  └── INSERT INTO outbox (...)       │
└─────────────────────────────────────┘
                 ↓
┌─────────────────────────────────────┐
│  OutboxPublisherService             │
│  (BackgroundService, 4 loops)       │
│  ├── PublishLoop                    │
│  ├── HeartbeatLoop                  │
│  ├── RebalanceLoop                  │
│  └── OrphanSweepLoop                │
└─────────────────────────────────────┘
                 ↓
         Message broker
        (Kafka / EventHub)
```

## The publish loop (pulling model)

The publisher doesn't receive push notifications. It **polls** the database on an adaptive interval.

### Step by step

1. **Fetch a batch** — `FetchBatchAsync` selects up to `BatchSize` (default 100) messages from partitions owned by this publisher. This is a pure `SELECT`—no row locking or lease stamping. A version ceiling filter (`xmin` on PostgreSQL, `RowVersion` on SQL Server) withholds rows from in-flight write transactions to prevent out-of-order publishing.

2. **Group by destination** — Messages are grouped by `(topic_name, partition_key)`. Each group runs through its own in-memory retry loop.

3. **Check circuit breaker** — If the circuit is open for a topic, the group is skipped without burning an attempt. Messages stay in the outbox and will be picked up when the circuit closes.

4. **Send to broker** — For each group, messages are sent via `IOutboxTransport.SendAsync`, ordered by `SequenceNumber` (equals insert order).

5. **Delete on success** — Successfully sent messages are deleted from the outbox.

6. **Classify failures** — On a send exception, the transport's `IsTransient(Exception)` decides what happens next:
   - **Transient** (broker unreachable, timeout) — the circuit breaker records a failure. The attempt counter stays where it is. The retry loop sleeps with exponential backoff and tries again.
   - **Non-transient** (serialization error, permission denied, message too large) — the in-memory attempt counter is incremented. Once it reaches `MaxPublishAttempts`, the group is dead-lettered inline via `DeadLetterAsync` in the same publish loop iteration.

7. **No safety net needed** — Partition ownership is the sole isolation mechanism. There are no leases to release on cancellation or crash. All retry state lives on the worker thread's stack, so a crash just restarts the attempt budget on the next fetch.

### Adaptive polling

| Condition                                         | Behavior                                             |
| ------------------------------------------------- | ---------------------------------------------------- |
| Empty batch                                       | Double the poll interval (up to `MaxPollIntervalMs`) |
| Messages found and published                      | Reset to `MinPollIntervalMs`                         |
| Messages found, nothing published (circuits open) | Double the poll interval                             |

This prevents hot loops during idle periods and ensures fast draining when messages are flowing.

## How ordering works

Ordering is guaranteed within a `(topic, partition_key)` pair. Three mechanisms work together.

### 1. Store-level ordering

`FetchBatchAsync` orders by `sequence_number`, which equals the order in which rows were INSERTed (since `sequence_number` is a `BIGINT IDENTITY` / `bigserial` that increments monotonically at insert and is never modified by retries). Callers MUST insert messages in the order they want them delivered — `event_datetime_utc` is preserved on the row as a debug/forensics field but does NOT influence delivery order.

A version ceiling filter ensures rows from in-flight write transactions aren't visible yet. PostgreSQL uses `xmin < pg_snapshot_xmin(pg_current_snapshot())` and SQL Server uses `RowVersion < MIN_ACTIVE_ROWVERSION()`. This prevents the scenario where Transaction #2 commits before Transaction #1 and its rows are published out of order.

### 2. Batch-level ordering

Before sending each group to the transport, the publish loop sorts by `SequenceNumber`:

```csharp
var groupMessages = group.OrderBy(m => m.SequenceNumber).ToList();
```

This guarantees insert-order delivery within each group, consistent with the store-level `FetchBatchAsync` ordering.

### 3. Partition ownership (single-writer guarantee)

Each logical partition is owned by exactly one publisher at a time. `FetchBatchAsync` only returns messages from owned partitions. Combined with a grace period during ownership transfers, this ensures no two publishers ever concurrently process the same partition key's messages. Partition ownership is the sole isolation mechanism—there are no per-message leases.

### What's not ordered

Messages with **different partition keys** have no ordering guarantee. If you need strict ordering across multiple keys, use the same partition key for all of them.

### Deduplication

A successfully sent message that fails to delete (e.g., database hiccup after the broker acknowledged) will be re-delivered on the next poll since the row remains in the outbox. Consumers must be idempotent. Use `SequenceNumber` as a deduplication key.

## Publisher groups

A publisher group is a configuration unit that binds a publisher to a specific outbox table. Each group operates on its own `outbox` + `dead_letter` table pair while sharing the `outbox_publishers` and `outbox_partitions` infrastructure tables.

### Why groups exist

Without groups, all publishers in a deployment compete for the same outbox table. If a service has multiple bounded contexts (orders, notifications, billing), their messages share a single retry/dead-letter pipeline. A poison message storm in one context can starve the others.

Groups solve this by letting each context have its own outbox table, its own dead-letter table, and its own partition pool — while still sharing the lightweight infrastructure tables that coordinate publisher ownership.

### How outbox_table_name works

The `outbox_publishers` and `outbox_partitions` tables have an `outbox_table_name` column that scopes all operations. This column stores the bare table name of the outbox data table the publisher reads from (e.g., `outbox`, `orders_outbox`).

```
outbox_partitions
┌───────────────────┬──────────────┬────────────────────┐
│ outbox_table_name │ partition_id │ owner_publisher_id  │
├───────────────────┼──────────────┼────────────────────┤
│ outbox            │ 0            │ outbox-publisher-a1 │
│ outbox            │ 1            │ outbox-publisher-a1 │
│ orders_outbox     │ 0            │ orders-pub-b2      │
│ orders_outbox     │ 1            │ orders-pub-b2      │
│ orders_outbox     │ 2            │ orders-pub-b3      │
└───────────────────┴──────────────┴────────────────────┘
```

Every query that touches these tables — heartbeat, rebalance, fetch, sweep — filters by `outbox_table_name`. This means:

- Publishers registered against `outbox` only see and compete for `outbox` partitions
- Publishers registered against `orders_outbox` only see and compete for `orders_outbox` partitions
- Fair-share calculation is scoped: `ceil(partitions_for_this_table / active_publishers_for_this_table)`
- Each outbox table can have a different number of partitions

### The group is the outbox table assignment

A group doesn't introduce a new abstraction — it simply answers "which outbox table does this publisher read from?" The group name (`"orders"`) is a human-readable label used for:

- Prefixing the publisher ID: `orders-outbox-publisher-{guid}`
- Keying DI services so each group has its own store, transport, and health state
- Naming the meter: `orders.Outbox.Publisher`
- Tagging logs with `OutboxGroup = "orders"`

If two groups point to the same outbox table, they share the same partition pool and rebalance together. This is intentional — the `outbox_table_name` is the real grouping key, not the group name.

### Registration

```csharp
// Default — single outbox table, no group name
services.AddOutbox(config, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});

// Multiple groups — each with its own outbox + dead_letter table
services.AddOutbox("orders", config, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});

services.AddOutbox("notifications", config, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});
```

Each `AddOutbox` call registers a fully independent `OutboxPublisherService` instance with its own 4 background loops, its own circuit breakers, its own health check, and its own metrics. Groups don't share any runtime state.

### Database layout with groups

```
Shared infrastructure (one set per database):
  outbox_publishers    — all publishers across all groups, scoped by outbox_table_name
  outbox_partitions    — all partitions across all groups, scoped by outbox_table_name

Per-group data tables:
  outbox               — default group's messages
  outbox_dead_letter   — default group's dead letters
  orders_outbox        — orders group's messages
  orders_outbox_dead_letter — orders group's dead letters
```

The infrastructure tables use `SharedSchemaName` (defaults to the same schema as data tables). Per-group data tables use the `TablePrefix` to derive their names.

## Partition ownership

Messages are distributed across logical partitions using a hash of the partition key:

```
partition_id = hash(partition_key) % total_partitions
```

PostgreSQL uses `hashtext()` (computed at query time) and SQL Server uses `CHECKSUM()` (precomputed as a persisted `PartitionId` column). Different functions, producing different mappings. Both providers seed 64 partitions by default (the modulus is baked into SQL Server's computed column formula). Each group can have a different partition count, configured in the install script.

### How ownership is distributed

Three background loops manage partition assignment:

**Heartbeat loop** (every 10s) — Updates `last_heartbeat_utc` for this publisher and cancels any pending grace period on its partitions. This signals "I'm still alive."

**Rebalance loop** (every 30s) — Computes a fair share (`ceil(total_partitions / active_publishers)`) and:

1. Marks stale publishers' partitions with a grace period
2. Claims unowned or grace-expired partitions up to the fair share
3. Releases excess partitions if over the fair share

**Orphan sweep loop** (every 60s) — Claims partitions with no owner at all, as a safety net between rebalance cycles.

### The grace period

When a publisher stops heartbeating, its partitions aren't immediately reassigned. Instead, they enter a grace period (`PartitionGracePeriodSeconds`, default 60s). This gives the original publisher time to finish any in-flight work before a new owner takes over.

If the original publisher comes back and heartbeats, the grace period is cancelled. If it doesn't, the partition becomes claimable.

### Parallel publish threads

Each publisher runs `PublishThreadCount` (default 4) concurrent workers within its single publish loop. The coordinator polls a batch of messages, groups them by `(TopicName, PartitionKey)`, and assigns each group to a worker using:

```
worker_index = (hash(partition_key) % total_partitions) % PublishThreadCount
```

This guarantees that messages with the same partition key are always processed by the same worker, preserving ordering. Different partition keys can be processed concurrently across workers.

The 3 housekeeping loops (heartbeat, rebalance, orphan sweep) remain single-instance. Only the publish work is parallelized.

When using [publisher groups](#publisher-groups), each group has its own independent `PublishThreadCount`. Configure it per group in the `AddOutbox` registration.

Set `PublishThreadCount = 1` to revert to sequential single-thread processing.

## Circuit breaker

Each topic has its own circuit breaker that prevents retry-count burn during broker outages.

### States

| State        | Behavior                                                                                             |
| ------------ | ---------------------------------------------------------------------------------------------------- |
| **Closed**   | Normal operation. Failures are counted.                                                              |
| **Open**     | Messages are skipped—they stay in the outbox, the in-memory attempt counter isn't touched. No sends attempted. |
| **HalfOpen** | Timer expired. One probe batch is allowed through.                                                   |

### Transitions

- **Closed → Open** — After `CircuitBreakerFailureThreshold` (default 3) consecutive failures.
- **Open → HalfOpen** — After `CircuitBreakerOpenDurationSeconds` (default 30s) elapses.
- **HalfOpen → Closed** — One successful send.
- **HalfOpen → Open** — One failed send.

### Why it matters

Without the circuit breaker, a broker outage would burn through every message's retry budget. Valid messages would get dead-lettered for infrastructure problems that had nothing to do with the message. The circuit breaker holds messages in place until the broker recovers, and the `IsTransient` classification backs it up by making sure broker timeouts never burn a non-transient attempt either.

## Dead-lettering

Retry state lives entirely in memory. Each `(topic, partition_key)` group tracks an `attempt` counter on the worker thread's stack. Only **non-transient** failures (the transport's `IsTransient` returned `false`) increment it. Once it reaches `MaxPublishAttempts` (default 5), the group is dead-lettered inline in the same publish loop iteration — no background sweep, no separate pass.

The inline path calls `IOutboxStore.DeadLetterAsync(sequenceNumbers, attemptCount, lastError, ct)`, which atomically deletes the rows from the outbox and inserts them into the dead-letter table with the final attempt count and last error message. If the publisher crashes mid-retry, the messages stay in the outbox and the next fetch starts over with a fresh attempt budget.

Dead-lettered messages can be replayed via `IDeadLetterManager.ReplayAsync`, which moves them back to the outbox so the publish loop picks them up again.

## Health checks

The publisher exposes an ASP.NET Core health check under the name `"outbox"`.

| State         | Condition                                 |
| ------------- | ----------------------------------------- |
| **Unhealthy** | Publish loop not running                  |
| **Unhealthy** | Heartbeat stale (> 3× heartbeat interval) |
| **Unhealthy** | No polls (> 3× max poll interval)         |
| **Degraded**  | Circuit breaker open for any topic        |
| **Degraded**  | Loops have restarted                      |
| **Healthy**   | All checks pass                           |

The health check reflects the publisher's internal state only. Database and broker connectivity are left to infrastructure health checks.

When using publisher groups, each group registers its own health check (e.g., `"outbox-orders"`, `"outbox-notifications"`) with independent state — one group going unhealthy doesn't affect others.

## Loop coordination and restart

All four loops run inside a shared cancellation scope. If any loop exits (crash or completion), the linked `CancellationTokenSource` cancels the others. After all loops stop:

1. If the service is shutting down, exit cleanly.
2. Otherwise, increment `ConsecutiveLoopRestarts` and apply exponential backoff (2s, 4s, 8s, ..., capped at 2 minutes).
3. If restarts exceed 5, call `StopApplication()` to let the orchestrator (Kubernetes, systemd) handle recovery.
4. The restart counter resets after 30 continuous seconds of healthy polling.

## Observability

### Metrics (meter: `"Outbox"`, or `"{groupName}.Outbox"` when using publisher groups)

| Metric                                 | Type           | Description                   |
| -------------------------------------- | -------------- | ----------------------------- |
| `outbox.messages.published`            | Counter        | Messages successfully sent    |
| `outbox.messages.dead_lettered`        | Counter        | Messages moved to dead letter |
| `outbox.messages.pending`              | Gauge          | Current pending count         |
| `outbox.publish.failures`              | Counter        | Failed publish attempts       |
| `outbox.circuit_breaker.state_changes` | Counter        | Circuit state transitions     |
| `outbox.publish.duration`              | Histogram (ms) | Transport send duration       |
| `outbox.poll.duration`                 | Histogram (ms) | Database fetch duration       |
| `outbox.poll.batch_size`               | Histogram      | Messages per batch            |

### Distributed tracing

An `ActivitySource` named `"Outbox"` (or `"{groupName}.Outbox"` when using publisher groups) creates one activity per `(topic, partitionKey)` group, tagged with `messaging.destination.name` and `messaging.batch.message_count`.

## Event handler callbacks

Implement `IOutboxEventHandler` to receive lifecycle notifications:

| Callback                            | When                                                                                       |
| ----------------------------------- | ------------------------------------------------------------------------------------------ |
| `OnMessagePublishedAsync`           | After successful send, before delete                                                       |
| `OnPublishFailedAsync`              | After a publish failure, carrying a `PublishFailureReason` (`RetriesExhausted`, `CircuitOpened`) |
| `OnMessageDeadLetteredAsync`        | After a message is dead-lettered inline                                                    |
| `OnCircuitBreakerStateChangedAsync` | After any circuit state change                                                             |
| `OnRebalanceAsync`                  | After partition rebalance                                                                  |

All callbacks are individually try/caught—exceptions never affect message fate. Health state is always updated _before_ the callback fires.

## Message interceptors

Two interception points let you transform messages without touching the publish loop:

- **`IOutboxMessageInterceptor`** — Runs after the store returns a message, before transport dispatch. Can mutate `Payload`, `Headers`, and `PayloadContentType`. Routing fields (`TopicName`, `PartitionKey`, `SequenceNumber`) are read-only.
- **`ITransportMessageInterceptor<TMessage>`** — Runs after the core interceptor, on the transport-specific envelope (Kafka `Message<string, byte[]>` or EventHub `EventData`).

Both use lazy context allocation—zero overhead when no interceptor matches a message.

## Configuration

All publisher options are in the `"Outbox:Publisher"` configuration section and support hot-reload via `IOptionsMonitor`.

| Option                              | Default | Description                                                       |
| ----------------------------------- | ------- | ----------------------------------------------------------------- |
| `GroupName`                         | null    | Publisher group name (null = default)                             |
| `BatchSize`                         | 100     | Messages per poll                                                 |
| `MaxPublishAttempts`                | 5       | Max non-transient attempts before dead-lettering                  |
| `RetryBackoffBaseMs`                | 100     | Base delay for exponential retry backoff                          |
| `RetryBackoffMaxMs`                 | 2000    | Ceiling for exponential retry backoff                             |
| `PublishThreadCount`                | 4       | Concurrent publish workers per poll                               |
| `MinPollIntervalMs`                 | 100     | Fastest poll rate                                                 |
| `MaxPollIntervalMs`                 | 5000    | Slowest poll rate                                                 |
| `HeartbeatIntervalMs`               | 10000   | Heartbeat frequency                                               |
| `HeartbeatTimeoutSeconds`           | 30      | Staleness threshold                                               |
| `PartitionGracePeriodSeconds`       | 60      | Grace period before partition takeover                            |
| `RebalanceIntervalMs`               | 30000   | Rebalance frequency                                               |
| `OrphanSweepIntervalMs`             | 60000   | Orphan sweep frequency                                            |
| `CircuitBreakerFailureThreshold`    | 3       | Transient failures before circuit opens                           |
| `CircuitBreakerOpenDurationSeconds` | 30      | Open duration before half-open probe                              |

### Store options

PostgreSQL options are in `"Outbox:PostgreSql"`, SQL Server options in `"Outbox:SqlServer"`.

| Option                      | Default            | Store | Description                |
| --------------------------- | ------------------ | ----- | -------------------------- |
| `ConnectionString`          | null               | Both  | Database connection string |
| `SchemaName`                | `"public"`/`"dbo"` | Both  | Schema name                |
| `TablePrefix`               | `""`               | Both  | Prefix for all table names |
| `CommandTimeoutSeconds`     | 30                 | Both  | SQL command timeout        |
| `TransientRetryMaxAttempts` | 6                  | Both  | Max retry attempts         |
| `TransientRetryBackoffMs`   | 1000               | Both  | Base backoff (ms)          |

### Group-scoped config paths

When using publisher groups, config binds from group-scoped paths first with a fallback to the shared path. For a group named `"orders"`:

- `Outbox:Orders:Publisher` overrides `Outbox:Publisher`
- `Outbox:Orders:PostgreSql` overrides `Outbox:PostgreSql`
- `Outbox:Orders:Kafka` overrides `Outbox:Kafka`

This lets you set shared defaults at the top level and override per group—for example, giving one group a different `ConnectionString` or `PublishThreadCount`.

### Validation rules

These are enforced at startup and will fail fast if violated:

- `HeartbeatTimeoutSeconds × 1000 ≥ HeartbeatIntervalMs × 3` — tolerates at least 2 missed heartbeats
- `MaxPollIntervalMs ≥ MinPollIntervalMs` — poll interval range is valid
