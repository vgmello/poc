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
│  (BackgroundService, 5 loops)       │
│  ├── PublishLoop                    │
│  ├── HeartbeatLoop                  │
│  ├── RebalanceLoop                  │
│  ├── OrphanSweepLoop               │
│  └── DeadLetterSweepLoop           │
└─────────────────────────────────────┘
                 ↓
         Message broker
        (Kafka / EventHub)
```

## The publish loop (pulling model)

The publisher doesn't receive push notifications. It **polls** the database on an adaptive interval.

### Step by step

1. **Lease a batch** — `LeaseBatchAsync` selects up to `BatchSize` (default 100) messages from partitions owned by this publisher. Each selected row gets a `lease_owner` stamp and a `leased_until_utc` expiry. Rows locked by other publishers are skipped (`SKIP LOCKED` in PostgreSQL, `READPAST` in SQL Server).

2. **Separate poison messages** — Messages with `retry_count >= MaxRetryCount` are immediately dead-lettered with `CancellationToken.None`.

3. **Group by destination** — Healthy messages are grouped by `(topic_name, partition_key)`.

4. **Check circuit breaker** — If the circuit is open for a topic, the group is released without incrementing the retry count.

5. **Send to broker** — For each group, messages are sent via `IOutboxTransport.SendAsync`, ordered by `SequenceNumber`.

6. **Delete on success** — Successfully sent messages are deleted from the outbox.

7. **Release on failure** — Failed messages have their lease cleared and `retry_count` incremented.

8. **Safety net** — A `finally` block releases any messages that weren't explicitly handled (covers mid-batch crashes and cancellation).

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

`LeaseBatchAsync` orders by `(event_datetime_utc, event_ordinal)`. The `event_ordinal` field (a `SMALLINT`) breaks ties when multiple events share the same timestamp—common when a single transaction inserts several events.

### 2. Batch-level ordering

Before sending each group to the transport, the publish loop sorts by `SequenceNumber`:

```csharp
var groupMessages = group.OrderBy(m => m.SequenceNumber).ToList();
```

This guarantees monotonic ordering even when a batch contains messages from different poll cycles (e.g., after a lease expiry re-lease).

### 3. Partition ownership (single-writer guarantee)

Each logical partition is owned by exactly one publisher at a time. `LeaseBatchAsync` only returns messages from owned partitions. Combined with a grace period during ownership transfers, this ensures no two publishers ever concurrently process the same partition key's messages.

### What's not ordered

Messages with **different partition keys** have no ordering guarantee. If you need strict ordering across multiple keys, use the same partition key for all of them.

### Deduplication

A successfully sent message that fails to delete (e.g., database hiccup after the broker acknowledged) will be re-delivered on the next poll. Consumers must be idempotent. Use `SequenceNumber` as a deduplication key.

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

Every query that touches these tables — heartbeat, rebalance, lease, sweep — filters by `outbox_table_name`. This means:

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
    outbox.UsePostgreSql(connectionFactory);
    outbox.UseKafka();
});

// Multiple groups — each with its own outbox + dead_letter table
services.AddOutbox("orders", config, outbox =>
{
    outbox.UsePostgreSql(connectionFactory, o => o.TablePrefix = "orders_");
    outbox.UseKafka();
});

services.AddOutbox("notifications", config, outbox =>
{
    outbox.UsePostgreSql(connectionFactory, o => o.TablePrefix = "notifications_");
    outbox.UseKafka();
});
```

Each `AddOutbox` call registers a fully independent `OutboxPublisherService` instance with its own 5 background loops, its own circuit breakers, its own health check, and its own metrics. Groups don't share any runtime state.

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

PostgreSQL uses `hashtext()` and SQL Server uses `CHECKSUM()`—different functions, producing different mappings. By default, 32 partitions are seeded per outbox table. Each group can have a different partition count, configured in the install script.

### How ownership is distributed

Three background loops manage partition assignment:

**Heartbeat loop** (every 10s) — Updates `last_heartbeat_utc` for this publisher and cancels any pending grace period on its partitions. This signals "I'm still alive."

**Rebalance loop** (every 30s) — Computes a fair share (`ceil(total_partitions / active_publishers)`) and:

1. Marks stale publishers' partitions with a grace period
2. Claims unowned or grace-expired partitions up to the fair share
3. Releases excess partitions if over the fair share

**Orphan sweep loop** (every 60s) — Claims partitions with no owner at all, as a safety net between rebalance cycles.

### The grace period

When a publisher stops heartbeating, its partitions aren't immediately reassigned. Instead, they enter a grace period (`PartitionGracePeriodSeconds`, default 60s). The grace period must be strictly longer than `LeaseDurationSeconds` (default 45s) to prevent two publishers from processing the same partition simultaneously.

If the original publisher comes back and heartbeats, the grace period is cancelled. If it doesn't, the partition becomes claimable.

## Circuit breaker

Each topic has its own circuit breaker that prevents retry-count burn during broker outages.

### States

| State        | Behavior                                                                        |
| ------------ | ------------------------------------------------------------------------------- |
| **Closed**   | Normal operation. Failures are counted.                                         |
| **Open**     | Messages are released _without_ incrementing `retry_count`. No sends attempted. |
| **HalfOpen** | Timer expired. One probe batch is allowed through.                              |

### Transitions

- **Closed → Open** — After `CircuitBreakerFailureThreshold` (default 3) consecutive failures.
- **Open → HalfOpen** — After `CircuitBreakerOpenDurationSeconds` (default 30s) elapses.
- **HalfOpen → Closed** — One successful send.
- **HalfOpen → Open** — One failed send.

### Why it matters

Without the circuit breaker, a broker outage would burn through every message's retry budget. Messages would get dead-lettered even though they're perfectly valid. The circuit breaker holds messages in place until the broker recovers.

## Dead-lettering

Messages that exhaust their retry budget (`retry_count >= MaxRetryCount`) are moved to a `dead_letter` table. This happens in two ways:

1. **Inline** — The publish loop detects a poison message during batch processing and calls `DeadLetterAsync`.
2. **Background sweep** — The `DeadLetterSweepLoop` (every 60s) finds messages that a publisher failed to dead-letter (e.g., the publisher crashed after incrementing the retry count but before moving the message).

Dead-lettered messages can be replayed via `IDeadLetterManager.ReplayAsync`, which moves them back to the outbox with `retry_count` reset to 0.

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

All five loops run inside a shared cancellation scope. If any loop exits (crash or completion), the linked `CancellationTokenSource` cancels the others. After all loops stop:

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
| `outbox.poll.duration`                 | Histogram (ms) | Database lease duration       |
| `outbox.poll.batch_size`               | Histogram      | Messages per batch            |

### Distributed tracing

An `ActivitySource` named `"Outbox"` (or `"{groupName}.Outbox"` when using publisher groups) creates one activity per `(topic, partitionKey)` group, tagged with `messaging.destination.name` and `messaging.batch.message_count`.

## Event handler callbacks

Implement `IOutboxEventHandler` to receive lifecycle notifications:

| Callback                            | When                                         |
| ----------------------------------- | -------------------------------------------- |
| `OnMessagePublishedAsync`           | After successful send, before delete         |
| `OnPublishFailedAsync`              | After transport failure, after lease release |
| `OnMessageDeadLetteredAsync`        | After a message is dead-lettered             |
| `OnCircuitBreakerStateChangedAsync` | After any circuit state change               |
| `OnRebalanceAsync`                  | After partition rebalance                    |

All callbacks are individually try/caught—exceptions never affect message fate. Health state is always updated _before_ the callback fires.

## Message interceptors

Two interception points let you transform messages without touching the publish loop:

- **`IOutboxMessageInterceptor`** — Runs after the store returns a message, before transport dispatch. Can mutate `Payload`, `Headers`, and `PayloadContentType`. Routing fields (`TopicName`, `PartitionKey`, `SequenceNumber`) are read-only.
- **`ITransportMessageInterceptor<TMessage>`** — Runs after the core interceptor, on the transport-specific envelope (Kafka `Message<string, byte[]>` or EventHub `EventData`).

Both use lazy context allocation—zero overhead when no interceptor matches a message.

## Configuration

All publisher options are in the `"Outbox:Publisher"` configuration section and support hot-reload via `IOptionsMonitor`.

| Option                              | Default | Description                            |
| ----------------------------------- | ------- | -------------------------------------- |
| `GroupName`                         | null    | Publisher group name (null = default)  |
| `BatchSize`                         | 100     | Messages per poll                      |
| `LeaseDurationSeconds`              | 45      | Lock duration per message              |
| `MaxRetryCount`                     | 5       | Retries before dead-lettering          |
| `MinPollIntervalMs`                 | 100     | Fastest poll rate                      |
| `MaxPollIntervalMs`                 | 5000    | Slowest poll rate                      |
| `HeartbeatIntervalMs`               | 10000   | Heartbeat frequency                    |
| `HeartbeatTimeoutSeconds`           | 30      | Staleness threshold                    |
| `PartitionGracePeriodSeconds`       | 60      | Grace period before partition takeover |
| `RebalanceIntervalMs`               | 30000   | Rebalance frequency                    |
| `OrphanSweepIntervalMs`             | 60000   | Orphan sweep frequency                 |
| `DeadLetterSweepIntervalMs`         | 60000   | Dead-letter sweep frequency            |
| `CircuitBreakerFailureThreshold`    | 3       | Failures before circuit opens          |
| `CircuitBreakerOpenDurationSeconds` | 30      | Open duration before half-open probe   |

### Validation rules

These are enforced at startup and will fail fast if violated:

- `PartitionGracePeriodSeconds > LeaseDurationSeconds` — prevents dual partition processing
- `HeartbeatTimeoutSeconds × 1000 ≥ HeartbeatIntervalMs × 3` — tolerates at least 2 missed heartbeats
- `MaxRetryCount > CircuitBreakerFailureThreshold` — circuit breaker can activate before dead-lettering
