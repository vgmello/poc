# EventHub Outbox Pattern — SQL Server: Architecture Specification

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
| Partition ordering | Events sharing the same `PartitionKey` should arrive at EventHub in application-controlled causal order (`EventDateTimeUtc, EventOrdinal`). This requires explicit partition affinity (see §4). |
| Poison message isolation | Persistently failing messages must be isolated and dead-lettered after a configurable retry threshold. |
| Dynamic scaling | Publisher instances can join or leave without manual reconfiguration. Partition ownership rebalances automatically. |

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                     Application / Domain Layer                      │
│                                                                     │
│  BEGIN TRANSACTION                                                  │
│    INSERT dbo.Outbox (...)   ← guaranteed atomicity with business   │
│  COMMIT                        data in the same database            │
└────────────────────────────┬────────────────────────────────────────┘
                             │ SQL Server (same DB)
                             ▼
┌─────────────────────────────────────────────────────────────────────┐
│   dbo.Outbox          dbo.OutboxPartitions      dbo.OutboxProducers │
│   (event buffer)      (affinity map)            (heartbeat registry)│
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
- **Partition affinity** — a thin join-through table (`OutboxPartitions`) assigns each EventHub partition to exactly one publisher at a time. This preserves per-partition-key ordering.
- **Heartbeat registry** — publishers register in `OutboxProducers` and refresh a heartbeat. Missing heartbeats trigger orphan sweep and partition reassignment.
- **Poison message dead-lettering** — a `RetryCount` column and a `dbo.OutboxDeadLetter` table isolate persistently failing messages.
- **TVP-based batch delete** — `dbo.SequenceNumberList` (a Table-Valued Parameter type) replaces `OPENJSON` for efficient, statistics-friendly batch deletes.
- **Adaptive polling** — publishers back off exponentially on empty polls and reset on the first non-empty poll. This eliminates SQL Server load during idle periods without sacrificing low latency during bursts.
- **Circuit breaker** — per-topic circuit breaker pauses publishing when EventHub is unavailable, preventing retry count burnout and unnecessary dead-lettering during outages.
- **Orphan sweep** — a background loop claims unowned partitions (e.g., after a publisher crash) up to the publisher's fair share, preventing backlog buildup.

---

## 3. Ordering Semantics

### What the design guarantees

- **Within a partition**: rows assigned to the same EventHub partition and processed by the same publisher instance are sent in `EventDateTimeUtc, EventOrdinal` (application-controlled causal) order within a single EventHub batch. This decouples publish ordering from database insertion order, which is critical when using ORMs (e.g., Entity Framework) that do not guarantee INSERT order within a single `SaveChanges()` call.
- **Across partitions**: no global ordering guarantee. Different partitions are independent.

### What the design does NOT guarantee without partition affinity

With naive `READPAST` and multiple concurrent publishers:

- Publisher A leases rows 1, 2, 3 (PartitionKey = "Order-123").
- Publisher B leases rows 4, 5, 6 (PartitionKey = "Order-123").
- Publisher B finishes first → row 4 arrives at EventHub before row 1.

This breaks per-key ordering. **Partition affinity is required if ordered delivery matters.**

### Ordering contract with partition affinity enabled

When partition affinity is active (see §4):

- A given EventHub partition number is owned by at most one publisher at any time.
- A **unified poll query** leases both fresh (unleased) and expired-lease rows in a single pass, ordered by `EventDateTimeUtc, EventOrdinal`. This guarantees that events are published in the causal order determined by the application, regardless of the order in which the database assigned `SequenceNumber` (IDENTITY) values. Expired rows from a crashed publisher are naturally interleaved by their application timestamp, preserving per-PartitionKey ordering even during crash recovery.
- `RetryCount` is conditionally incremented only for rows that were previously leased (recovery rows). Fresh rows keep `RetryCount = 0`.
- EventHub sub-batches within a PartitionKey group use **all-or-nothing** semantics: if any sub-batch fails, none of the group's rows are deleted. This prevents partial-send gaps that would allow newer rows to be processed before the failed rows, at the cost of potential duplicates (acceptable under at-least-once delivery).
- During rebalance (ownership transfer), a grace period ensures the outgoing publisher completes or expires any in-flight leases before the incoming publisher starts polling that partition.

### Why not `SequenceNumber` for ordering?

`SequenceNumber` is a `BIGINT IDENTITY(1,1)` column whose value is assigned at INSERT time by SQL Server. When using an ORM like Entity Framework, a single `SaveChanges()` call may insert multiple outbox rows in non-deterministic order. Two events for the same partition key — where Event A should causally precede Event B — could receive swapped sequence numbers (B gets a lower number than A). Ordering by `SequenceNumber` would then publish them out of order.

`EventDateTimeUtc` and `EventOrdinal` are set by the application, which knows the correct causal order. `EventDateTimeUtc` provides the primary ordering dimension, and `EventOrdinal` breaks ties within the same timestamp (e.g., multiple events in a single `SaveChanges()` batch).

### Ordering contract without partition affinity

If `WithPartitionAffinity = false` (simple mode, multiple topics, ordering not required):

- Global ordering is **best-effort** and not guaranteed.
- Consumers must tolerate out-of-order delivery.
- This must be explicitly documented in consumer contracts.

---

## 4. Partition Affinity

### Motivation

EventHub routes messages to partitions by hashing `PartitionKey`. For ordered delivery, all messages with the same `PartitionKey` must be sent to the same EventHub partition, and they must be sent in insertion order. With multiple concurrent publishers, this requires each publisher to "own" a set of EventHub partitions exclusively.

### Partition assignment model

```
dbo.OutboxPartitions
  PartitionId       INT  (0-based EventHub partition number)
  OwnerProducerId   NVARCHAR(128)  NULL  (NULL = unowned)
  OwnedSinceUtc     DATETIME2(3)  NULL
  GraceExpiresUtc   DATETIME2(3)  NULL  (handover protection window)
```

A publisher that owns partition P processes only rows whose `PartitionKey` hashes to P. Rows for unowned partitions (orphans) are picked up by any available publisher during the orphan sweep.

### Rebalance protocol

**Trigger**: A new publisher joins, an existing publisher's heartbeat expires, or the partition count changes.

**Steps**:

1. **Stale detection** — Identify partitions whose owner has not heartbeated within `@HeartbeatTimeoutSeconds`.
2. **Grace period** — Set `GraceExpiresUtc = SYSUTCDATETIME() + @PartitionGracePeriodSeconds` on the stale partition. This prevents the new owner from starting before in-flight leases held by the old owner can expire.
3. **Grace recovery** — If the stale producer recovers and resumes heartbeating, its heartbeat clears `GraceExpiresUtc` on its owned partitions, preventing other publishers from stealing partitions that are actively being served.
4. **Claim** — After the grace period elapses, any publisher can claim the partition via an optimistic CAS update (`UPDATE ... WHERE OwnerProducerId = @OldOwner OR GraceExpiresUtc < SYSUTCDATETIME()`).
5. **Fair-share** — Each publisher claims `CEILING(TotalPartitions / ActiveProducers)` partitions. Publishers with more than their fair share release excess partitions (mark them as stealable by setting `OwnerProducerId = NULL`).

### Orphan sweep

Rows in the outbox whose `PartitionKey` maps to an unowned partition accumulate as orphans. A dedicated orphan sweep loop (runs every `@OrphanSweepIntervalSeconds`, default 60s) identifies partitions with `OwnerProducerId IS NULL` and claims them up to the publisher's fair share using `UPDATE TOP ... WITH (UPDLOCK)`. The publisher begins processing the orphaned rows on its next poll cycle (the lease query joins `dbo.OutboxPartitions` server-side, so no local cache is needed). This prevents backlog buildup during partial outages.

---

## 5. Crash Resilience

### Failure matrix

| Crash point | State after crash | Recovery |
|---|---|---|
| Before lease UPDATE | Row never leased. `LeasedUntilUtc IS NULL`. | Normal poll picks it up immediately. |
| After lease UPDATE, before EventHub send | Row leased, not sent. | Lease expires → unified poll (same partition owner) re-leases on next cycle. No data loss. Ordering preserved because the unified poll orders by `EventDateTimeUtc, EventOrdinal`, so expired rows with earlier application timestamps are always processed first. |
| After EventHub send, before DELETE | Row leased, already sent. | Lease expires → unified poll re-leases → re-sent (duplicate). At-least-once, as designed. Ordering preserved because the unified poll processes rows in application-controlled causal order. |
| After DELETE | Clean. Nothing to recover. | — |
| After partition claim, before processing any rows | Partition marked owned, no rows leased. | Next publisher sees expired heartbeat, starts grace period, reclaims partition. |

### Lease duration sizing

```
@LeaseDurationSeconds >= @EventHubSendTimeoutSeconds × 2 + @NetworkJitterBuffer
```

Recommended: `@EventHubSendTimeoutSeconds = 15`, `@LeaseDurationSeconds = 45`.

If the lease duration is too short relative to a slow EventHub send:
- The lease expires while the send is in-flight.
- A second publisher re-leases and re-sends (duplicate) before the first publisher's send completes.
- Both publishers then try to delete. Only one succeeds (the `LeaseOwner` guard prevents the other from deleting rows it no longer owns). Correct behavior, but unnecessary duplicate.

### Heartbeat failure and producer registration race

- Producers register in `dbo.OutboxProducers` on startup using `MERGE` to handle concurrent registration from multiple instances.
- Heartbeat renewal uses an `UPDATE WHERE ProducerId = @Self`.
- If two instances start simultaneously with the same `ProducerId` (misconfiguration), the second `MERGE` is a no-op (idempotent insert). The heartbeat from both will keep the registration alive; partition claims remain stable.
- To prevent race conditions during partition handover, all partition claim queries use `UPDLOCK` on the `OutboxPartitions` row.

---

## 6. Poison Message Handling

### Problem

A message that consistently fails to publish (malformed payload, EventHub rejects the partition key, schema validation failure) will:

1. Be leased.
2. Fail to publish.
3. Lease expires.
4. Unified poll re-leases it (incrementing `RetryCount`).
5. Fail again. Repeat indefinitely.

This wastes publisher cycles and can block progress on rows with higher sequence numbers that share the same lease batch.

### Solution: `RetryCount` + dead-letter table

`dbo.Outbox` has a `RetryCount INT NOT NULL DEFAULT 0` column.

The unified poll conditionally increments `RetryCount` only for rows that were previously leased (`LeasedUntilUtc IS NOT NULL`). Fresh rows (`LeasedUntilUtc IS NULL`) keep `RetryCount = 0`. Rows released by the circuit breaker also have `LeasedUntilUtc = NULL`, so they correctly avoid the increment.

When `RetryCount >= @MaxRetryCount` (default: 5), the dead-letter sweep atomically deletes the row from `dbo.Outbox` and inserts it into `dbo.OutboxDeadLetter` using a single `DELETE ... OUTPUT INTO` statement. This guarantees the exact rows removed from the outbox are the same rows inserted into the dead-letter table, preventing data loss from concurrent operations.

The dead-letter table is monitored separately and requires manual intervention (inspect, fix, replay, or discard).

### Dead-letter sweep

The publisher detects poison messages inline: the unified poll's conditional `RetryCount` increment may push a row to `@MaxRetryCount`, and the publisher immediately dead-letters it before attempting to publish. A background sweep (runs every `@DeadLetterSweepIntervalSeconds`, default 60s) acts as a safety net for rows that slip through (e.g., publisher crashes after the SQL increment but before the inline dead-letter).

The sweep uses `DELETE ... OUTPUT INTO` with `ROWLOCK, READPAST` hints to ensure atomicity and avoid blocking concurrent publishers. This replaces the earlier INSERT + DELETE pattern, which could operate on different row sets under concurrency.

### Retry count semantics

- `RetryCount = 0` — Row has never been re-leased after a lease expiry. It may have been leased and published successfully, or it may be a fresh row that was never leased.
- `RetryCount = N` — Row has been re-leased N times after lease expiry without successful publication. The unified poll's `CASE WHEN LeasedUntilUtc IS NOT NULL` ensures only expired-lease rows are incremented.
- Rows released by the circuit breaker (via `ReleaseLeasedRows`) have `LeasedUntilUtc` reset to `NULL`, so they are treated as fresh rows and do not increment `RetryCount`. This prevents dead-lettering during transient EventHub outages.

---

## 7. Dynamic Scaling and Rebalance Protocol

### Producer registration

On startup, each publisher:

1. Generates a unique `ProducerId` (e.g., `{hostname}:{processId}:{Guid}`).
2. Upserts into `dbo.OutboxProducers` (`MERGE` on `ProducerId`).
3. Begins heartbeat loop (every `@HeartbeatIntervalSeconds`, default 10s).
4. Runs initial partition claim.

### Fair-share calculation

```
FairShare = CEILING(TotalPartitions / ActiveProducers)
```

Where `ActiveProducers` = count of producers with `LastHeartbeatUtc >= SYSUTCDATETIME() - @HeartbeatTimeoutSeconds`.

A publisher with more than `FairShare` partitions releases excess ones by setting `OwnerProducerId = NULL`. The claim and release run inside a single SQL transaction to prevent concurrent publishers from observing an inconsistent partition assignment.

### Stealable partitions

A publisher that needs more partitions (currently owns fewer than `FairShare`) attempts to steal unowned partitions. The steal is an atomic CAS:

```sql
UPDATE dbo.OutboxPartitions
SET    OwnerProducerId = @Self,
       OwnedSinceUtc   = SYSUTCDATETIME(),
       GraceExpiresUtc = NULL
WHERE  PartitionId     = @PartitionId
  AND  (OwnerProducerId IS NULL OR GraceExpiresUtc < SYSUTCDATETIME())
```

If two publishers race to steal the same partition, only one wins. The other will try the next unowned partition.

### Graceful shutdown

On orderly shutdown, the publisher:

1. Stops polling for new leases.
2. Waits for all in-flight EventHub sends to complete (or timeout).
3. Deletes its `OutboxProducers` row.
4. Sets `OwnerProducerId = NULL` on all owned partitions (no grace period needed — the outgoing publisher is done).

This allows immediate re-distribution of partitions to remaining publishers without waiting for heartbeat timeout.

### Forced rebalance

An operator can trigger an immediate rebalance by truncating `dbo.OutboxProducers`. All publishers will re-register and re-claim partitions on their next heartbeat cycle.

---

## 8. Adaptive Polling

### Algorithm

```
consecutiveEmptyPolls = 0
backoffMs = @MinPollIntervalMs  (default: 100ms)

loop:
  rows = LeaseNextBatch()
  if rows.Count == 0:
    consecutiveEmptyPolls++
    backoffMs = MIN(backoffMs * 2, @MaxPollIntervalMs)  (default max: 5000ms)
    Sleep(backoffMs)
  else:
    consecutiveEmptyPolls = 0
    backoffMs = @MinPollIntervalMs
    Publish(rows)
    Delete(rows)
```

### Benefits

- At steady state (continuous inserts), poll interval stays at minimum (100ms). End-to-end latency stays low.
- During idle periods, poll interval grows to 5 seconds, reducing SQL Server query overhead by ~50×.
- No configuration change needed when load changes. Self-tuning.

### Unified poll (replaces separate recovery path)

The publisher uses a single unified poll that picks up both fresh and expired-lease rows, ordered by `EventDateTimeUtc, EventOrdinal`. There is no separate recovery loop. Expired rows (from crashes or timeouts) naturally surface in the same adaptive poll — since they have earlier application timestamps, they are always processed before newer rows, preserving per-PartitionKey ordering.

---

## 9. Schema Reference

### `dbo.Outbox`

| Column | Type | Nullable | Default | Purpose |
|---|---|---|---|---|
| `SequenceNumber` | `BIGINT IDENTITY(1,1)` | NOT NULL | — | Clustered PK. Append-only. Used for row identity (delete, release, dead-letter). Not used for publish ordering — see `EventDateTimeUtc`/`EventOrdinal`. |
| `TopicName` | `NVARCHAR(256)` | NOT NULL | — | Target EventHub topic. |
| `PartitionKey` | `NVARCHAR(256)` | NOT NULL | — | EventHub partition key. Determines partition affinity bucket. |
| `EventType` | `NVARCHAR(256)` | NOT NULL | — | Discriminator for consumers. |
| `Headers` | `NVARCHAR(4000)` | NULL | — | JSON key-value blob → EventHub `Properties`. |
| `Payload` | `NVARCHAR(4000)` | NOT NULL | — | Event body. Intentionally capped at 4000 chars (~8 KB) to enable covering index INCLUDEs; `NVARCHAR(MAX)` cannot be included in nonclustered indexes, forcing key lookups on every poll. Producers must validate payload size before inserting. |
| `CreatedAtUtc` | `DATETIME2(3)` | NOT NULL | `SYSUTCDATETIME()` | Insert timestamp. Audit/diagnostic. |
| `EventDateTimeUtc` | `DATETIME2(3)` | NOT NULL | — | Application-controlled event timestamp. Determines publish ordering. Set by the producer, not the database. |
| `EventOrdinal` | `SMALLINT` | NOT NULL | `0` | Tie-breaker within the same `EventDateTimeUtc`. The application sets 0, 1, 2… for events in the same `SaveChanges()` batch. |
| `LeasedUntilUtc` | `DATETIME2(3)` | NULL | — | NULL = fresh. Past = expired. Future = active lease. |
| `LeaseOwner` | `NVARCHAR(128)` | NULL | — | ProducerId holding the current lease. |
| `RetryCount` | `INT` | NOT NULL | `0` | Times re-leased after lease expiry (conditionally incremented by unified poll). Drives dead-letter threshold. |

### `dbo.OutboxDeadLetter`

Same columns as `dbo.Outbox` (without `LeasedUntilUtc` / `LeaseOwner`) plus:

| Column | Type | Purpose |
|---|---|---|
| `EventDateTimeUtc` | `DATETIME2(3)` | Preserved from the original outbox row. |
| `EventOrdinal` | `SMALLINT` | Preserved from the original outbox row. |
| `DeadLetteredAtUtc` | `DATETIME2(3)` | When the row was moved to dead-letter. |
| `LastError` | `NVARCHAR(2000)` | Last exception message from the publisher. |

### `dbo.OutboxProducers`

| Column | Type | Purpose |
|---|---|---|
| `ProducerId` | `NVARCHAR(128)` | Unique publisher instance identifier. PK. |
| `RegisteredAtUtc` | `DATETIME2(3)` | When the producer registered. |
| `LastHeartbeatUtc` | `DATETIME2(3)` | Last heartbeat. Used to detect crashes. |
| `HostName` | `NVARCHAR(256)` | Diagnostic: hostname of publisher. |

### `dbo.OutboxPartitions`

| Column | Type | Purpose |
|---|---|---|
| `PartitionId` | `INT` | EventHub partition number (0-based). PK. |
| `OwnerProducerId` | `NVARCHAR(128)` | FK → `OutboxProducers.ProducerId`. NULL = unowned. |
| `OwnedSinceUtc` | `DATETIME2(3)` | When current owner claimed this partition. |
| `GraceExpiresUtc` | `DATETIME2(3)` | After this time, partition is stealable. Used during handover. |

### `dbo.SequenceNumberList` (TVP type)

```sql
CREATE TYPE dbo.SequenceNumberList AS TABLE (
    SequenceNumber BIGINT NOT NULL PRIMARY KEY
);
```

Used for batch deletes with proper cardinality estimates (replaces `OPENJSON` which always estimates 50 rows).

---

## 10. Index Design

### `IX_Outbox_Unleased` — unified poll (fresh rows arm)

```sql
CREATE NONCLUSTERED INDEX IX_Outbox_Unleased
ON dbo.Outbox (EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
WHERE LeasedUntilUtc IS NULL;
```

Filtered on `LeasedUntilUtc IS NULL`. Leading on `(EventDateTimeUtc, EventOrdinal)` to match the unified poll's `ORDER BY`. Covers all columns needed by the unified poll's fresh-row arm. Keeps the index small at steady state.

### `IX_Outbox_LeaseExpiry` — unified poll (expired rows arm)

```sql
CREATE NONCLUSTERED INDEX IX_Outbox_LeaseExpiry
ON dbo.Outbox (LeasedUntilUtc, EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
WHERE LeasedUntilUtc IS NOT NULL;
```

Filtered on `LeasedUntilUtc IS NOT NULL`, leading on `LeasedUntilUtc` for range seek, then `(EventDateTimeUtc, EventOrdinal)` for sort order. SQL Server can use an index union of `IX_Outbox_Unleased` and `IX_Outbox_LeaseExpiry` to serve the unified poll's `OR` predicate.

### Write amplification per message lifecycle

| Operation | Clustered | IX_Unleased | IX_LeaseExpiry |
|---|---|---|---|
| INSERT | +1 | +1 | — |
| UPDATE (lease) | modify | −1 | +1 |
| DELETE | −1 | — | −1 |

**Total: 4 index operations per message.** Acceptable for most workloads. Profile at >10K msg/sec.

---

## 11. Publisher Queries Reference

All queries are parameterized. See `EventHubOutbox.sql` for complete, runnable SQL.

| Query | Trigger | Purpose |
|---|---|---|
| `LeaseBatch` | Unified poll (adaptive interval) | Lease both fresh and expired-lease rows in `EventDateTimeUtc, EventOrdinal` order; conditionally increment `RetryCount` for recovery rows |
| `DeletePublishedTVP` | After successful EventHub send | Delete by TVP with `LeaseOwner` guard |
| `ReleaseLeasedRows` | Circuit breaker open | Release leases on skipped rows via TVP; rows return unleased without incrementing `RetryCount` |
| `DeadLetterSweep` | Background sweep (every 60s) and inline detection | Move rows at `RetryCount >= @MaxRetryCount` to dead-letter |
| `RegisterProducer` | On startup | Upsert producer heartbeat record |
| `HeartbeatProducer` | Every `@HeartbeatIntervalSeconds` | Refresh `LastHeartbeatUtc`; clear `GraceExpiresUtc` on owned partitions |
| `UnregisterProducer` | On graceful shutdown | Delete producer record, release partitions (via `SqlTransaction`) |
| `Rebalance` | After registration; after rebalance trigger | Atomic claim + release in a single transaction: claim unowned/stale partitions up to fair share, then release excess |
| `OrphanSweep` | Every `@OrphanSweepIntervalSeconds` | Claim unowned partitions up to fair share |

---

## 12. Health Checks and Monitoring

### Depth and lease distribution

```sql
SELECT
    COUNT(*)                                                             AS TotalRows,
    SUM(CASE WHEN LeasedUntilUtc IS NULL THEN 1 ELSE 0 END)            AS Unleased,
    SUM(CASE WHEN LeasedUntilUtc >= SYSUTCDATETIME() THEN 1 ELSE 0 END) AS ActivelyLeased,
    SUM(CASE WHEN LeasedUntilUtc < SYSUTCDATETIME() THEN 1 ELSE 0 END)  AS ExpiredLeases,
    MIN(CreatedAtUtc)                                                    AS OldestMessage,
    DATEDIFF_BIG(MILLISECOND, MIN(CreatedAtUtc), SYSUTCDATETIME())       AS MaxLatencyMs,
    MAX(RetryCount)                                                      AS MaxRetryCount
FROM dbo.Outbox;
```

*Note*: Uses `DATEDIFF_BIG(MILLISECOND, ...)` for sub-second precision. The original `DATEDIFF(SECOND, ...)` truncates milliseconds.

### Per-topic depth

```sql
SELECT TopicName, COUNT(*) AS Pending
FROM dbo.Outbox
GROUP BY TopicName
ORDER BY Pending DESC;
```

### Active producers

```sql
SELECT
    ProducerId,
    HostName,
    RegisteredAtUtc,
    LastHeartbeatUtc,
    DATEDIFF_BIG(MILLISECOND, LastHeartbeatUtc, SYSUTCDATETIME()) AS HeartbeatAgeMs
FROM dbo.OutboxProducers
ORDER BY LastHeartbeatUtc DESC;
```

### Partition ownership

```sql
SELECT
    p.PartitionId,
    p.OwnerProducerId,
    pr.HostName,
    p.OwnedSinceUtc,
    p.GraceExpiresUtc,
    CASE WHEN p.OwnerProducerId IS NULL THEN 'UNOWNED'
         WHEN pr.ProducerId IS NULL THEN 'ORPHANED'
         WHEN p.GraceExpiresUtc > SYSUTCDATETIME() THEN 'IN_GRACE'
         ELSE 'OWNED'
    END AS Status
FROM dbo.OutboxPartitions p
LEFT JOIN dbo.OutboxProducers pr
    ON pr.ProducerId = p.OwnerProducerId
ORDER BY p.PartitionId;
```

### Dead-letter queue depth

```sql
SELECT COUNT(*) AS DeadLetterCount, MIN(DeadLetteredAtUtc) AS OldestDeadLetter
FROM dbo.OutboxDeadLetter;
```

### SLA breach alert (example threshold: >5 minutes lag)

```sql
SELECT COUNT(*) AS SlaBreachCount
FROM dbo.Outbox
WHERE CreatedAtUtc < DATEADD(MINUTE, -5, SYSUTCDATETIME());
```

---

## 13. Operational Edge Cases

### Backlog buildup (EventHub outage)

During an EventHub outage:

- Unified poll returns rows, lease updates succeed, EventHub sends fail.
- Leases expire; on next poll cycle the unified query re-leases and conditionally increments `RetryCount`.
- After `@MaxRetryCount` retries, rows are dead-lettered inline or by the background sweep.

**Mitigation**: A per-topic circuit breaker is implemented in `OutboxPublisher`. After `@CircuitBreakerFailureThreshold` consecutive send failures (default: 3), the circuit opens and the publisher skips batches for that topic for `@CircuitBreakerOpenDurationSeconds` (default: 30s). When a batch is skipped because the circuit is open, the publisher **releases the leases** on the affected rows (resets `LeasedUntilUtc` and `LeaseOwner` to NULL) so they return to the unleased pool without incrementing `RetryCount`. After the open duration elapses, the circuit enters half-open state and allows one probe send. On success, the circuit closes and normal publishing resumes. On failure, the circuit re-opens.

This prevents dead-lettering during transient EventHub outages while preserving at-least-once delivery semantics.

### Publisher over-scaling (too many publishers relative to partitions)

If `ActiveProducers > TotalPartitions`, some publishers will own zero partitions in affinity mode. They remain idle (heartbeating but not polling). No harm done, but wasteful.

**Mitigation**: Auto-scale publisher instances based on `Unleased + ExpiredLeases` outbox depth.

### `IDENTITY` gap warning

`BIGINT IDENTITY(1,1)` is gap-safe for this use case: the design never relies on contiguous sequence numbers. Gaps from rollbacks are expected and harmless. `SequenceNumber` is used solely for row identity (delete, release, dead-letter), not for publish ordering.

**Operational rule**: Never run `DBCC CHECKIDENT` with a reseed value on `dbo.Outbox` in production. It can cause duplicate key violations if the table still contains rows above the new seed.

### Index fragmentation

High insert + delete throughput generates fragmentation in the filtered nonclustered indexes. Because the table is small at steady state, `ALTER INDEX ALL ON dbo.Outbox REBUILD` is near-instant and can be scheduled during low-traffic windows (e.g., nightly).

Monitor with:

```sql
SELECT index_id, avg_fragmentation_in_percent, page_count
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.Outbox'), NULL, NULL, 'LIMITED')
ORDER BY avg_fragmentation_in_percent DESC;
```

### Ghost cleanup overhead

After heavy deletes, SQL Server's ghost cleanup task processes deleted records asynchronously. This can cause brief spikes in CPU/IO during cleanup passes. Acceptable under normal operation. Avoid scheduling `REBUILD` during peak hours to prevent a double-whammy of rebuild + ghost cleanup.

### Partition hash collisions

Multiple `PartitionKey` values may hash to the same EventHub partition. The affinity model assigns ownership at the *partition number* level, not the key level, so all keys mapping to partition P are handled by P's owner. No special handling needed.

### CHECKSUM vs EventHub internal hashing

The SQL-side partition assignment uses `ABS(CHECKSUM(PartitionKey)) % TotalPartitions`. EventHub uses its own internal hashing algorithm (MurmurHash-based). These two hashes are **not equivalent**: SQL partition bucket N does not correspond to EventHub partition N for the same `PartitionKey`.

**Why this doesn't break correctness**: ordering depends on the same `PartitionKey` always being routed to the same publisher — the SQL CHECKSUM guarantees this deterministically. The actual EventHub partition is determined by the EventHub SDK when `PartitionKey` is set on `CreateBatchOptions`. The SQL partition number is a *logical work distribution bucket*, not a physical EventHub partition identifier.

**Operational note**: the `dbo.OutboxPartitions.PartitionId` values are logical bucket IDs, not actual EventHub partition numbers. Monitoring queries that display partition ownership show bucket assignments, not EventHub partition-level ownership.

---

## 14. Transactional Coupling Contract for Producers

### The golden rule

> **Every `INSERT` into `dbo.Outbox` must occur within the same database transaction as the business entity change it represents.**

If the producer inserts into `dbo.Outbox` outside the business transaction (e.g., after commit), a crash between commit and insert results in **permanent message loss**.

### Correct pattern

```csharp
using var tx = connection.BeginTransaction();

// 1. Business logic
await connection.ExecuteAsync("UPDATE dbo.Orders SET Status = 'Confirmed' WHERE Id = @Id",
    new { Id = orderId }, tx);

// 2. Outbox event — same transaction
//    EventDateTimeUtc and EventOrdinal control publish ordering (not SequenceNumber).
//    Set EventOrdinal to 0, 1, 2… for multiple events in the same transaction.
await connection.ExecuteAsync(@"
    INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Headers, Payload,
                            EventDateTimeUtc, EventOrdinal)
    VALUES (@TopicName, @PartitionKey, @EventType, @Headers, @Payload,
            @EventDateTimeUtc, @EventOrdinal)",
    new { TopicName = "orders", PartitionKey = orderId.ToString(),
          EventType = "OrderConfirmed", Headers = (string?)null,
          Payload = JsonSerializer.Serialize(orderEvent),
          EventDateTimeUtc = DateTime.UtcNow, EventOrdinal = (short)0 }, tx);

tx.Commit();
```

### Anti-patterns

| Anti-pattern | Risk |
|---|---|
| Insert outbox row after `COMMIT` | Message lost if process crashes between commit and insert. |
| Insert outbox row in a separate transaction | Message lost if outer transaction rolls back after inner commits. |
| Insert outbox row inside a `try/catch` that swallows exceptions | Outbox row inserted for events that were never persisted. |
| Using `NOCOUNT ON` and ignoring row count on outbox insert | Errors silently swallowed. |

### Idempotency requirement

Because of at-least-once delivery, **all event consumers must be idempotent**. The recommended pattern is to include a `MessageId` in the event payload or headers (e.g., `SequenceNumber` as a string) and use a deduplicated inbox table on the consumer side.

---

## 15. Performance Tuning Parameters

| Parameter | Default | Notes |
|---|---|---|
| `@BatchSize` | 100 | Rows per lease batch. Balance between round-trip overhead and lock hold time. |
| `@LeaseDurationSeconds` | 45 | Must exceed EventHub send timeout with margin (see §5). |
| `@MaxRetryCount` | 5 | Dead-letter threshold. Lower = faster isolation; higher = more resilience to transient failures. |
| `@MinPollIntervalMs` | 100 | Minimum adaptive poll interval. |
| `@MaxPollIntervalMs` | 5000 | Maximum adaptive poll interval (idle backoff ceiling). |
| `@HeartbeatIntervalSeconds` | 10 | Producer heartbeat frequency. |
| `@HeartbeatTimeoutSeconds` | 30 | After this without a heartbeat, the producer is considered dead. |
| `@PartitionGracePeriodSeconds` | 60 | Grace window for partition handover. Must exceed `@LeaseDurationSeconds`. |
| `@OrphanSweepIntervalSeconds` | 60 | How often to scan for rows in unowned partitions. |
| `@DeadLetterSweepIntervalSeconds` | 60 | Safety-net dead-letter sweep interval. |
| `@EventHubSendTimeoutSeconds` | 15 | Timeout for a single EventHub `SendAsync` call. |
| `@EventHubMaxBatchBytes` | 1,048,576 | EventHub max batch size (1 MB). Publisher splits batches to stay under this limit. |
| `@CircuitBreakerFailureThreshold` | 3 | Consecutive send failures before the circuit opens for a topic. |
| `@CircuitBreakerOpenDurationSeconds` | 30 | Seconds to keep the circuit open before attempting a half-open probe. |
| `@SqlRetryMaxAttempts` | 3 | Maximum attempts for lease and delete operations on transient SQL errors (deadlock, timeout, Azure SQL transient). |
| `OnError` | `null` | `Action<string, Exception?>` callback for logging/observability. When null, errors are written to `Console.Error`. |
