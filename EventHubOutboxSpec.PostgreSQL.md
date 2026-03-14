# EventHub Outbox Pattern — PostgreSQL: Architecture Specification

## Table of Contents

1. [Requirements](#1-requirements)
2. [Architecture Overview](#2-architecture-overview)
3. [Ordering Semantics](#3-ordering-semantics)
4. [Partition Affinity](#4-partition-affinity)
5. [Crash Resilience](#5-crash-resilience)
6. [Poison Message Handling](#6-poison-message-handling)
7. [Dynamic Scaling and Rebalance Protocol](#7-dynamic-scaling-and-rebalance-protocol)
8. [Adaptive Polling](#8-adaptive-polling)
9. [Schema Reference](#9-schema-reference)
10. [Index Design](#10-index-design)
11. [Publisher Queries Reference](#11-publisher-queries-reference)
12. [Health Checks and Monitoring](#12-health-checks-and-monitoring)
13. [Operational Edge Cases](#13-operational-edge-cases)
14. [Transactional Coupling Contract for Producers](#14-transactional-coupling-contract-for-producers)
15. [Performance Tuning Parameters](#15-performance-tuning-parameters)
16. [PostgreSQL-Specific Considerations](#16-postgresql-specific-considerations)

---

## 1. Requirements

| Requirement | Detail |
|---|---|
| Transactional outbox | Events are inserted inside the producer's business transaction. They are only published to EventHub after the transaction commits. |
| High insert throughput | Minimal contention on the write path. Insert path must scale to thousands of events per second without latch hotspots. |
| Concurrent publishers | Horizontal scaling with no external coordinator. N publisher instances can run simultaneously. |
| At-least-once delivery | Duplicates are acceptable; message loss is not. Consumers must be idempotent. |
| Crash resilience | A publisher can crash at any point without causing data loss or stalling the pipeline. |
| Post-publish cleanup | Published records are deleted from the outbox. The table remains a transient buffer, not a log. |
| Multi-topic support | Each record targets a specific EventHub topic. |
| Header propagation | Custom metadata (correlation IDs, content type, trace context) flows to EventHub `Properties`. |
| Partition ordering | Events sharing the same `PartitionKey` should arrive at EventHub in application-controlled causal order (`event_datetime_utc, event_ordinal`). This requires explicit partition affinity (see §4). |
| Poison message isolation | Persistently failing messages must be isolated and dead-lettered after a configurable retry threshold. |
| Dynamic scaling | Publisher instances can join or leave without manual reconfiguration. Partition ownership rebalances automatically. |

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                     Application / Domain Layer                      │
│                                                                     │
│  BEGIN;                                                             │
│    INSERT INTO outbox (...)   ← guaranteed atomicity with business  │
│  COMMIT;                        data in the same database           │
└────────────────────────────┬────────────────────────────────────────┘
                             │ PostgreSQL (same DB)
                             ▼
┌─────────────────────────────────────────────────────────────────────┐
│   outbox                outbox_partitions      outbox_producers     │
│   (event buffer)        (affinity map)         (heartbeat registry) │
└──────────────────────────────┬──────────────────────────────────────┘
                               │ Publisher instances poll
          ┌────────────────────┼────────────────────┐
          ▼                    ▼                    ▼
    Publisher-A          Publisher-B           Publisher-C
    (owns parts 0,1,2)   (owns parts 3,4,5)   (owns parts 6,7)
          │                    │                    │
          └────────────────────┼────────────────────┘
                               │ EventHub SDK
                               ▼
                     Azure EventHub Topics
```

### Key design choices

- **Lease-based work distribution** — each publisher acquires an exclusive time-boxed lease on a batch of rows before publishing. This requires no external coordination.
- **Partition affinity** — a thin join-through table (`outbox_partitions`) assigns each EventHub partition to exactly one publisher at a time. This preserves per-partition-key ordering.
- **Heartbeat registry** — publishers register in `outbox_producers` and refresh a heartbeat. Missing heartbeats trigger orphan sweep and partition reassignment.
- **Poison message dead-lettering** — a `retry_count` column and an `outbox_dead_letter` table isolate persistently failing messages.
- **Array-based batch operations** — `ANY(@ids::bigint[])` provides efficient batch deletes and updates (replaces SQL Server's TVP pattern).
- **Adaptive polling** — publishers back off exponentially on empty polls and reset on the first non-empty poll. This eliminates database load during idle periods without sacrificing low latency during bursts.
- **Circuit breaker** — per-topic circuit breaker pauses publishing when EventHub is unavailable, preventing retry count burnout and unnecessary dead-lettering during outages.
- **Orphan sweep** — a background loop claims unowned partitions (e.g., after a publisher crash) up to the publisher's fair share, preventing backlog buildup.

---

## 3. Ordering Semantics

### What the design guarantees

- **Within a partition**: rows assigned to the same EventHub partition and processed by the same publisher instance are sent in `event_datetime_utc, event_ordinal` (application-controlled causal) order within a single EventHub batch. This decouples publish ordering from database insertion order, which is critical when using ORMs (e.g., Entity Framework) that do not guarantee INSERT order within a single `SaveChanges()` call.
- **Across partitions**: no global ordering guarantee. Different partitions are independent.

### Why not `sequence_number` for ordering?

`sequence_number` is a `BIGINT GENERATED ALWAYS AS IDENTITY` column whose value is assigned at INSERT time by PostgreSQL. When using an ORM, a single `SaveChanges()` call may insert multiple outbox rows in non-deterministic order. Two events for the same partition key — where Event A should causally precede Event B — could receive swapped sequence numbers. Ordering by `sequence_number` would then publish them out of order.

`event_datetime_utc` and `event_ordinal` are set by the application, which knows the correct causal order.

### Ordering contract with partition affinity enabled

When partition affinity is active (see §4):

- A given EventHub partition number is owned by at most one publisher at any time.
- A **unified poll query** leases both fresh (unleased) and expired-lease rows in a single pass, ordered by `event_datetime_utc, event_ordinal`.
- `retry_count` is conditionally incremented only for rows that were previously leased (recovery rows). Fresh rows keep `retry_count = 0`.
- EventHub sub-batches within a PartitionKey group use **all-or-nothing** semantics.
- During rebalance (ownership transfer), a grace period ensures the outgoing publisher completes or expires any in-flight leases before the incoming publisher starts polling that partition.

---

## 4. Partition Affinity

### Partition assignment model

```
outbox_partitions
  partition_id        INT   (0-based logical bucket number)
  owner_producer_id   VARCHAR(128)  NULL  (NULL = unowned)
  owned_since_utc     TIMESTAMPTZ(3)  NULL
  grace_expires_utc   TIMESTAMPTZ(3)  NULL  (handover protection window)
```

A publisher that owns partition P processes only rows whose `partition_key` hashes to P. Rows for unowned partitions (orphans) are picked up by any available publisher during the orphan sweep.

### Hash function: `hashtext()`

PostgreSQL's `hashtext()` function is used instead of SQL Server's `CHECKSUM()`. It returns a stable `int4` value for a given string input. The partition bucket is computed as `ABS(hashtext(partition_key)) % total_partitions`.

### Rebalance protocol

**Steps**:

1. **Stale detection** — Identify partitions whose owner has not heartbeated within `heartbeat_timeout_seconds`.
2. **Grace period** — Set `grace_expires_utc = NOW() + partition_grace_period_seconds` on the stale partition.
3. **Grace recovery** — If the stale producer recovers and resumes heartbeating, its heartbeat clears `grace_expires_utc`.
4. **Claim** — After the grace period elapses, any publisher can claim the partition using `FOR UPDATE SKIP LOCKED`.
5. **Fair-share** — Each publisher claims `CEIL(total_partitions / active_producers)` partitions.

---

## 5. Crash Resilience

### Failure matrix

| Crash point | State after crash | Recovery |
|---|---|---|
| Before lease UPDATE | Row never leased. `leased_until_utc IS NULL`. | Normal poll picks it up immediately. |
| After lease UPDATE, before EventHub send | Row leased, not sent. | Lease expires → unified poll re-leases on next cycle. |
| After EventHub send, before DELETE | Row leased, already sent. | Lease expires → re-sent (duplicate). At-least-once. |
| After DELETE | Clean. Nothing to recover. | — |

### Lease duration sizing

```
lease_duration_seconds >= event_hub_send_timeout_seconds × 2 + network_jitter_buffer
```

Recommended: `event_hub_send_timeout_seconds = 15`, `lease_duration_seconds = 45`.

---

## 6. Poison Message Handling

### Solution: `retry_count` + dead-letter table

The unified poll conditionally increments `retry_count` only for rows that were previously leased (`leased_until_utc IS NOT NULL`). When `retry_count >= max_retry_count` (default: 5), the row is moved to `outbox_dead_letter` using a CTE with `DELETE...RETURNING` piped into `INSERT`.

### PostgreSQL dead-letter atomicity

SQL Server uses `DELETE...OUTPUT INTO` for atomic dead-lettering. PostgreSQL achieves the same atomicity with:

```sql
WITH dead AS (
    DELETE FROM outbox
    WHERE sequence_number = @sequence_number
      AND lease_owner = @publisher_id
    RETURNING *
)
INSERT INTO outbox_dead_letter (...) SELECT ... FROM dead;
```

Both the DELETE and INSERT execute as a single atomic statement within the same CTE.

---

## 7. Dynamic Scaling and Rebalance Protocol

### Producer registration

On startup, each publisher:

1. Generates a unique `producer_id` (e.g., `{hostname}:{processId}:{Guid}`).
2. Upserts into `outbox_producers` (`INSERT...ON CONFLICT...DO UPDATE`).
3. Begins heartbeat loop.
4. Runs initial partition claim.

### Fair-share calculation

```
fair_share = CEIL(total_partitions / active_producers)
```

The claim and release run inside a single PostgreSQL transaction to prevent concurrent publishers from observing an inconsistent partition assignment.

### PostgreSQL locking for rebalance

Instead of SQL Server's `UPDATE TOP (@n) ... WITH (UPDLOCK)`, PostgreSQL uses:

```sql
WITH to_claim AS (
    SELECT partition_id
    FROM outbox_partitions
    WHERE owner_producer_id IS NULL
    ORDER BY partition_id
    LIMIT @to_acquire
    FOR UPDATE SKIP LOCKED
)
UPDATE outbox_partitions
SET owner_producer_id = @producer_id, ...
FROM to_claim
WHERE outbox_partitions.partition_id = to_claim.partition_id;
```

`FOR UPDATE SKIP LOCKED` provides the same concurrent-safe, non-blocking behaviour as SQL Server's `UPDLOCK + READPAST`.

---

## 8. Adaptive Polling

Same algorithm as the SQL Server version:

```
consecutiveEmptyPolls = 0
backoffMs = min_poll_interval_ms  (default: 100ms)

loop:
  rows = LeaseNextBatch()
  if rows.Count == 0:
    consecutiveEmptyPolls++
    backoffMs = MIN(backoffMs * 2, max_poll_interval_ms)  (default max: 5000ms)
    Sleep(backoffMs)
  else:
    consecutiveEmptyPolls = 0
    backoffMs = min_poll_interval_ms
    Publish(rows)
    Delete(rows)
```

---

## 9. Schema Reference

### `outbox`

| Column | Type | Nullable | Default | Purpose |
|---|---|---|---|---|
| `sequence_number` | `BIGINT GENERATED ALWAYS AS IDENTITY` | NOT NULL | — | PK. Append-only. Row identity for delete/release/dead-letter. Not used for ordering. |
| `topic_name` | `VARCHAR(256)` | NOT NULL | — | Target EventHub topic. |
| `partition_key` | `VARCHAR(256)` | NOT NULL | — | EventHub partition key. |
| `event_type` | `VARCHAR(256)` | NOT NULL | — | Discriminator for consumers. |
| `headers` | `VARCHAR(4000)` | NULL | — | JSON key-value blob → EventHub `Properties`. |
| `payload` | `VARCHAR(4000)` | NOT NULL | — | Event body. Capped at 4000 chars to enable covering index INCLUDEs. |
| `created_at_utc` | `TIMESTAMPTZ(3)` | NOT NULL | `clock_timestamp()` | Insert timestamp. |
| `event_datetime_utc` | `TIMESTAMPTZ(3)` | NOT NULL | — | Application-controlled ordering key. |
| `event_ordinal` | `SMALLINT` | NOT NULL | `0` | Tie-breaker within same `event_datetime_utc`. |
| `leased_until_utc` | `TIMESTAMPTZ(3)` | NULL | — | NULL = fresh. Past = expired. Future = active lease. |
| `lease_owner` | `VARCHAR(128)` | NULL | — | producer_id holding the current lease. |
| `retry_count` | `INT` | NOT NULL | `0` | Times re-leased after lease expiry. Drives dead-letter threshold. |

### `outbox_dead_letter`

Same columns as `outbox` (without `leased_until_utc` / `lease_owner`) plus:

| Column | Type | Purpose |
|---|---|---|
| `dead_lettered_at_utc` | `TIMESTAMPTZ(3)` | When the row was moved to dead-letter. |
| `last_error` | `VARCHAR(2000)` | Last exception message from the publisher. |

### `outbox_producers`

| Column | Type | Purpose |
|---|---|---|
| `producer_id` | `VARCHAR(128)` | Unique publisher instance identifier. PK. |
| `registered_at_utc` | `TIMESTAMPTZ(3)` | When the producer registered. |
| `last_heartbeat_utc` | `TIMESTAMPTZ(3)` | Last heartbeat. Used to detect crashes. |
| `host_name` | `VARCHAR(256)` | Diagnostic: hostname of publisher. |

### `outbox_partitions`

| Column | Type | Purpose |
|---|---|---|
| `partition_id` | `INT` | Logical bucket number (0-based). PK. |
| `owner_producer_id` | `VARCHAR(128)` | FK → `outbox_producers.producer_id`. NULL = unowned. |
| `owned_since_utc` | `TIMESTAMPTZ(3)` | When current owner claimed this partition. |
| `grace_expires_utc` | `TIMESTAMPTZ(3)` | After this time, partition is stealable. |

---

## 10. Index Design

### `ix_outbox_unleased` — unified poll (fresh rows arm)

```sql
CREATE INDEX ix_outbox_unleased
ON outbox (event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, headers, payload, retry_count, created_at_utc)
WHERE leased_until_utc IS NULL;
```

Partial index on `leased_until_utc IS NULL`. PostgreSQL has supported partial indexes since version 7.2 and INCLUDE columns since version 11.

### `ix_outbox_lease_expiry` — unified poll (expired rows arm)

```sql
CREATE INDEX ix_outbox_lease_expiry
ON outbox (leased_until_utc, event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, headers, payload, retry_count, created_at_utc)
WHERE leased_until_utc IS NOT NULL;
```

### Write amplification per message lifecycle

| Operation | Heap | ix_unleased | ix_lease_expiry |
|---|---|---|---|
| INSERT | +1 | +1 | — |
| UPDATE (lease) | modify (HOT if possible) | −1 | +1 |
| DELETE | mark dead | — | −1 |

PostgreSQL's HOT (Heap-Only Tuple) updates can avoid index maintenance when the updated columns are not in the index keys. Since `leased_until_utc` changes the partial index filter, HOT cannot be used for the lease update — but the index operations are minimal.

---

## 11. Publisher Queries Reference

| Query | Trigger | Purpose |
|---|---|---|
| `LeaseBatch` | Unified poll (adaptive interval) | CTE with FOR UPDATE SKIP LOCKED + UPDATE...RETURNING |
| `DeletePublished` | After successful EventHub send | DELETE WHERE sequence_number = ANY(@ids) |
| `ReleaseLeasedRows` | Circuit breaker open | UPDATE WHERE sequence_number = ANY(@ids) |
| `DeadLetterSweep` | Background sweep (every 60s) | CTE DELETE...RETURNING + INSERT |
| `RegisterProducer` | On startup | INSERT...ON CONFLICT...DO UPDATE |
| `HeartbeatProducer` | Every heartbeat interval | UPDATE last_heartbeat_utc; clear grace_expires_utc |
| `UnregisterProducer` | On graceful shutdown | Within transaction: release partitions + delete producer |
| `Rebalance` | After registration; periodic | Transactional claim + release with FOR UPDATE SKIP LOCKED |
| `OrphanSweep` | Every orphan sweep interval | Claim unowned partitions via CTE + FOR UPDATE SKIP LOCKED |

---

## 12. Health Checks and Monitoring

### Depth and lease distribution

```sql
SELECT
    COUNT(*)                                                              AS total_rows,
    SUM(CASE WHEN leased_until_utc IS NULL THEN 1 ELSE 0 END)            AS unleased,
    SUM(CASE WHEN leased_until_utc >= NOW() THEN 1 ELSE 0 END)           AS actively_leased,
    SUM(CASE WHEN leased_until_utc < NOW() THEN 1 ELSE 0 END)            AS expired_leases,
    MIN(created_at_utc)                                                   AS oldest_message,
    EXTRACT(EPOCH FROM (NOW() - MIN(created_at_utc))) * 1000              AS max_latency_ms,
    MAX(retry_count)                                                      AS max_retry_count
FROM outbox;
```

### Active producers

```sql
SELECT
    producer_id,
    host_name,
    registered_at_utc,
    last_heartbeat_utc,
    EXTRACT(EPOCH FROM (NOW() - last_heartbeat_utc)) * 1000 AS heartbeat_age_ms
FROM outbox_producers
ORDER BY last_heartbeat_utc DESC;
```

### Partition ownership

```sql
SELECT
    p.partition_id,
    p.owner_producer_id,
    pr.host_name,
    p.owned_since_utc,
    p.grace_expires_utc,
    CASE WHEN p.owner_producer_id IS NULL THEN 'UNOWNED'
         WHEN pr.producer_id IS NULL THEN 'ORPHANED'
         WHEN p.grace_expires_utc > NOW() THEN 'IN_GRACE'
         ELSE 'OWNED'
    END AS status
FROM outbox_partitions p
LEFT JOIN outbox_producers pr ON pr.producer_id = p.owner_producer_id
ORDER BY p.partition_id;
```

---

## 13. Operational Edge Cases

### VACUUM and dead tuple cleanup

Unlike SQL Server (which uses ghost cleanup), PostgreSQL relies on VACUUM to reclaim space from deleted rows. With high insert + delete throughput:

- **Autovacuum** should handle steady-state workloads automatically.
- Monitor `n_dead_tup` in `pg_stat_user_tables` for the outbox table.
- If autovacuum cannot keep up, consider tuning `autovacuum_vacuum_scale_factor` and `autovacuum_vacuum_cost_delay` for the outbox table specifically.
- Manual `VACUUM (VERBOSE) outbox;` can be run during maintenance windows.

### Index bloat

PostgreSQL indexes can accumulate bloat from frequent inserts and deletes. Use `REINDEX CONCURRENTLY` for online rebuilds:

```sql
REINDEX INDEX CONCURRENTLY ix_outbox_unleased;
REINDEX INDEX CONCURRENTLY ix_outbox_lease_expiry;
```

Monitor index size relative to table size:

```sql
SELECT
    indexrelname AS index_name,
    pg_size_pretty(pg_relation_size(indexrelid)) AS index_size
FROM pg_stat_user_indexes
WHERE relname = 'outbox';
```

### IDENTITY gap warning

`BIGINT GENERATED ALWAYS AS IDENTITY` can have gaps from rollbacks, just like SQL Server's `IDENTITY`. This is expected and harmless — `sequence_number` is used solely for row identity, not for ordering.

### hashtext() vs CHECKSUM()

PostgreSQL's `hashtext()` and SQL Server's `CHECKSUM()` produce different hash values for the same input. This means the partition bucket assignment is **not portable** between SQL Server and PostgreSQL for the same `partition_key`. This is correct by design — the hash is only used for work distribution, not for mapping to physical EventHub partitions.

### Connection pooling

PostgreSQL connections are more expensive than SQL Server connections. Use a connection pooler:

- **Built-in**: Npgsql has built-in connection pooling (enabled by default via connection string).
- **External**: PgBouncer for high-concurrency deployments.

Ensure `Max Pool Size` in the connection string is sufficient for the number of concurrent operations (publish loop + heartbeat + rebalance + sweep loops).

---

## 14. Transactional Coupling Contract for Producers

### The golden rule

> **Every `INSERT` into `outbox` must occur within the same database transaction as the business entity change it represents.**

### Correct pattern (PostgreSQL with Npgsql)

```csharp
await using var tx = await connection.BeginTransactionAsync();

// 1. Business logic
await using (var cmd = new NpgsqlCommand(
    "UPDATE orders SET status = 'Confirmed' WHERE id = @id", connection, tx))
{
    cmd.Parameters.AddWithValue("@id", orderId);
    await cmd.ExecuteNonQueryAsync();
}

// 2. Outbox event — same transaction
await using (var cmd = new NpgsqlCommand(@"
    INSERT INTO outbox (topic_name, partition_key, event_type, headers, payload,
                        event_datetime_utc, event_ordinal)
    VALUES (@topic_name, @partition_key, @event_type, @headers, @payload,
            @event_datetime_utc, @event_ordinal)", connection, tx))
{
    cmd.Parameters.AddWithValue("@topic_name", "orders");
    cmd.Parameters.AddWithValue("@partition_key", orderId.ToString());
    cmd.Parameters.AddWithValue("@event_type", "OrderConfirmed");
    cmd.Parameters.Add(new NpgsqlParameter("@headers", NpgsqlDbType.Varchar) { Value = DBNull.Value });
    cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(orderEvent));
    cmd.Parameters.AddWithValue("@event_datetime_utc", DateTime.UtcNow);
    cmd.Parameters.AddWithValue("@event_ordinal", (short)0);
    await cmd.ExecuteNonQueryAsync();
}

await tx.CommitAsync();
```

### Idempotency requirement

Because of at-least-once delivery, **all event consumers must be idempotent**.

---

## 15. Performance Tuning Parameters

| Parameter | Default | Notes |
|---|---|---|
| `batch_size` | 100 | Rows per lease batch. |
| `lease_duration_seconds` | 45 | Must exceed EventHub send timeout with margin. |
| `max_retry_count` | 5 | Dead-letter threshold. |
| `min_poll_interval_ms` | 100 | Minimum adaptive poll interval. |
| `max_poll_interval_ms` | 5000 | Maximum adaptive poll interval (idle backoff ceiling). |
| `heartbeat_interval_ms` | 10,000 | Producer heartbeat frequency. |
| `heartbeat_timeout_seconds` | 30 | Crash detection window. |
| `partition_grace_period_seconds` | 60 | Must exceed `lease_duration_seconds`. |
| `orphan_sweep_interval_ms` | 60,000 | Scan for unowned partitions. |
| `dead_letter_sweep_interval_ms` | 60,000 | Safety-net dead-letter sweep. |
| `event_hub_send_timeout_seconds` | 15 | EventHub SendAsync timeout. |
| `event_hub_max_batch_bytes` | 1,048,576 | EventHub 1 MB limit. |
| `circuit_breaker_failure_threshold` | 3 | Failures before circuit opens. |
| `circuit_breaker_open_duration_seconds` | 30 | Circuit open duration. |
| `sql_command_timeout_seconds` | 30 | PostgreSQL command timeout. |
| `on_error` | `null` | Error callback; defaults to Console.Error. |

---

## 16. PostgreSQL-Specific Considerations

### SQL Server → PostgreSQL translation reference

| SQL Server Feature | PostgreSQL Equivalent | Notes |
|---|---|---|
| `IDENTITY(1,1)` | `GENERATED ALWAYS AS IDENTITY` | Same semantics, no gaps guarantee |
| `SYSUTCDATETIME()` | `NOW()` / `clock_timestamp()` | `NOW()` is transaction-level; `clock_timestamp()` is statement-level |
| `DATEADD(SECOND, n, ts)` | `ts + make_interval(secs => n)` | Or `ts + INTERVAL 'n seconds'` for literals |
| `DATEDIFF_BIG(MS, a, b)` | `EXTRACT(EPOCH FROM (b - a)) * 1000` | Returns double precision |
| `ROWLOCK, READPAST` | `FOR UPDATE SKIP LOCKED` | Row-level lock, skip locked rows |
| `UPDLOCK` | `FOR UPDATE` | Exclusive row lock |
| `UPDATE...OUTPUT` | `UPDATE...RETURNING` | Returns modified rows |
| `DELETE...OUTPUT INTO` | `WITH d AS (DELETE...RETURNING *) INSERT...SELECT FROM d` | CTE-based atomic pattern |
| `MERGE` | `INSERT...ON CONFLICT...DO UPDATE` | PostgreSQL upsert syntax |
| `TVP` | `ANY(@ids::bigint[])` | Array parameter via Npgsql |
| `TOP (@n)` | `LIMIT @n` | Standard SQL |
| `UPDATE TOP (@n)` | CTE with `LIMIT...FOR UPDATE SKIP LOCKED` | See rebalance queries |
| Filtered index | Partial index (`WHERE ...`) | Native support since PostgreSQL 7.2 |
| `INCLUDE` columns | `INCLUDE (...)` | Supported since PostgreSQL 11 |
| `NVARCHAR` | `VARCHAR` | PostgreSQL uses UTF-8 natively |
| `DATETIME2(3)` | `TIMESTAMPTZ(3)` | Timezone-aware, stored as UTC |
| `CHECKSUM()` | `hashtext()` | Returns int4, stable across sessions |
| `BEGIN TRANSACTION...COMMIT` | `BEGIN...COMMIT` | Explicit transaction blocks |
| `HOLDLOCK` | `SERIALIZABLE` isolation or advisory locks | Usually not needed with `FOR UPDATE` |

### TIMESTAMPTZ vs TIMESTAMP

This implementation uses `TIMESTAMPTZ(3)` (timezone-aware) rather than `TIMESTAMP(3)` (timezone-naive). PostgreSQL stores `TIMESTAMPTZ` internally as UTC and converts on display based on the session's `timezone` setting. This matches the SQL Server `DATETIME2` + `SYSUTCDATETIME()` pattern where all timestamps are UTC.

### Connection string format

```
Host=localhost;Port=5432;Database=outbox_test;Username=postgres;Password=YourPassword;
```

Key Npgsql connection string parameters:
- `Max Pool Size` — default 100, increase for high-concurrency
- `Timeout` — connection timeout in seconds
- `Command Timeout` — default command timeout
- `Keepalive` — TCP keepalive interval

### Minimum PostgreSQL version

This implementation requires **PostgreSQL 11+** for `INCLUDE` columns in indexes. All other features (`FOR UPDATE SKIP LOCKED`, partial indexes, `INSERT...ON CONFLICT`, CTEs with data-modifying statements) are available since PostgreSQL 9.5+.
