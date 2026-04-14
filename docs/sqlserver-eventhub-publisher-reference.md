# SQL Server + Event Hub publisher reference

Step-by-step breakdown of everything the outbox publisher does when backed by SQL Server and Azure Event Hubs—from startup to shutdown, with every query it runs.

## Configuration defaults

Before diving in, here are the options that drive the timings referenced throughout:

| Option                              | Default            | Used by                                  |
| ----------------------------------- | ------------------ | ---------------------------------------- |
| `PublisherName`                     | `outbox-publisher` | Publisher ID generation                  |
| `BatchSize`                         | 100                | Fetch query `TOP`                        |
| `MaxPublishAttempts`                | 5                  | Non-transient attempts before DLQ        |
| `RetryBackoffBaseMs`                | 100                | Base delay for in-memory retry loop      |
| `RetryBackoffMaxMs`                 | 2,000              | Ceiling for exponential retry backoff    |
| `PublishThreadCount`                | 4                  | Concurrent publish workers per poll      |
| `MinPollIntervalMs`                 | 100                | Publish loop (busy)                      |
| `MaxPollIntervalMs`                 | 5000               | Publish loop (idle)                      |
| `HeartbeatIntervalMs`               | 10,000             | Heartbeat loop delay                     |
| `HeartbeatTimeoutSeconds`           | 30                 | Staleness detection                      |
| `PartitionGracePeriodSeconds`       | 60                 | Partition takeover safety window         |
| `RebalanceIntervalMs`               | 30,000             | Rebalance loop delay                     |
| `OrphanSweepIntervalMs`             | 60,000             | Orphan sweep loop delay                  |
| `CircuitBreakerFailureThreshold`    | 3                  | Consecutive transient failures to trip   |
| `CircuitBreakerOpenDurationSeconds` | 30                 | How long the circuit stays open          |

Event Hub transport options:

| Option               | Default          | Purpose                                     |
| -------------------- | ---------------- | ------------------------------------------- |
| `ConnectionString`   | _(required)_     | Namespace-level Event Hub connection string |
| `MaxBatchSizeBytes`  | 1,048,576 (1 MB) | Max `EventDataBatch` size                   |
| `SendTimeoutSeconds` | 15               | Per-batch send timeout                      |

SQL Server store options:

| Option                      | Default   | Purpose                              |
| --------------------------- | --------- | ------------------------------------ |
| `SchemaName`                | `dbo`     | SQL schema for all tables            |
| `TablePrefix`               | _(empty)_ | Prefix prepended to table names      |
| `CommandTimeoutSeconds`     | 30        | SQL command timeout                  |
| `TransientRetryMaxAttempts` | 6         | Retry count for transient SQL errors |
| `TransientRetryBackoffMs`   | 1,000     | Base backoff between retries         |

All table names below assume the defaults (`dbo.Outbox`, `dbo.OutboxPublishers`, etc.). If you set `SchemaName` or `TablePrefix`, the actual names change accordingly.

---

## Phase 1: startup and registration

When the `OutboxPublisherService` starts, it generates a publisher ID and registers itself in SQL Server.

### 1.1 Generate publisher ID

Format: `{PublisherName}-{Guid:N}`

Example: `outbox-publisher-a1b2c3d4e5f6789012345678abcdef01`

### 1.2 Register publisher

The publisher inserts (or updates) its row in the publishers table using a `MERGE` with `HOLDLOCK` to handle restarts safely:

```sql
MERGE dbo.OutboxPublishers WITH (HOLDLOCK) AS target
USING (SELECT @PublisherId AS PublisherId, @HostName AS HostName) AS source
    ON target.PublisherId = source.PublisherId
WHEN MATCHED THEN
    UPDATE SET LastHeartbeatUtc = SYSUTCDATETIME(),
               HostName         = source.HostName
WHEN NOT MATCHED THEN
    INSERT (PublisherId, RegisteredAtUtc, LastHeartbeatUtc, HostName)
    VALUES (source.PublisherId, SYSUTCDATETIME(), SYSUTCDATETIME(), source.HostName);
```

**Parameters:**

- `@PublisherId` — the generated publisher ID
- `@HostName` — `Environment.MachineName`

If registration fails (database unavailable, network issues), the publisher retries with exponential backoff: `2s → 4s → 8s → ...` capped at 60s. It keeps retrying until it succeeds or the host shuts down.

### 1.3 Initialize circuit breaker

A `TopicCircuitBreaker` is created in memory (not persisted). It tracks failures per topic name and transitions through three states: **Closed → Open → HalfOpen → Closed**.

### 1.4 Launch parallel loops

After successful registration, the publisher creates a linked `CancellationTokenSource` and starts four parallel `Task`s (publish, heartbeat, rebalance, orphan sweep). If any loop exits—whether from a crash or an unexpected return—all loops are cancelled and the entire set restarts with exponential backoff (`2s → 4s → 8s → ...` capped at 2 minutes). After five consecutive restarts without 30 seconds of healthy operation, the publisher stops the host application.

---

## Phase 2: publish loop

The core loop—leases messages, sends them to Event Hub, and finalizes the outcome. Runs continuously until cancelled.

### 2.1 Get partition count (cached)

Before leasing, the publisher needs the total partition count. It's cached for 60 seconds to avoid hitting SQL Server on every poll:

```sql
SELECT COUNT(*) FROM dbo.OutboxPartitions;
```

If the count is 0, the publisher skips leasing and waits.

### 2.2 Fetch a batch

A pure `SELECT` that reads messages from owned partitions without locking or updating rows:

```sql
SELECT TOP (@BatchSize)
    o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType,
    o.Headers, o.Payload, o.PayloadContentType,
    o.EventDateTimeUtc,
    o.CreatedAtUtc
FROM dbo.Outbox o WITH (NOLOCK)
WHERE o.PartitionId IN (
    SELECT op.PartitionId
    FROM dbo.OutboxPartitions op
    WHERE op.OutboxTableName = @OutboxTableName
      AND op.OwnerPublisherId = @PublisherId
      AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
)
  AND o.RowVersion < MIN_ACTIVE_ROWVERSION()
ORDER BY o.PartitionId, o.SequenceNumber;
```

**Parameters:**

- `@BatchSize` — max messages to fetch (default 100)
- `@PublisherId` — this publisher's ID
- `@OutboxTableName` — the outbox table name for this publisher group

**What the filters do:**

| Filter                                         | Purpose                                                            |
| ---------------------------------------------- | ------------------------------------------------------------------ |
| `o.PartitionId IN (subquery)`                  | Only fetch from partitions this publisher owns (precomputed hash)  |
| `op.GraceExpiresUtc IS NULL OR < NOW`          | Don't fetch from partitions still in grace period                  |
| `RowVersion < MIN_ACTIVE_ROWVERSION()`         | Version ceiling — withholds rows from in-flight write transactions |
| `ORDER BY PartitionId, SequenceNumber`         | Strict ordering within a partition key (equals insert order)       |

There is no retry-count filter — retry state lives in memory, so every row in the outbox is a candidate for delivery. Messages that burned attempts on a previous fetch simply restart their budget when re-fetched after a crash.

**No row locking:** The query uses no lock hints (`ROWLOCK`, `READPAST`, etc.). Partition ownership is the sole isolation mechanism — each publisher only fetches from its owned partitions, so there is no risk of two publishers reading the same rows. This avoids lock manager overhead, which is the dominant performance cost on SQL Server.

**Precomputed partition hash:** The `PartitionId` column is a persisted computed column (`ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % 64`). The hash is computed once at INSERT time, not on every SELECT. The index `IX_Outbox_Pending` leads with `PartitionId`, enabling an Index Seek instead of a full table scan.

**Version ceiling:** The `RowVersion < MIN_ACTIVE_ROWVERSION()` filter prevents the scenario where Transaction #2 commits before Transaction #1 and its rows are published out of order. Any concurrent write transaction in the database temporarily pauses processing of new inserts until it commits.

### 2.3 Adaptive polling

If the batch is empty, the poll interval doubles (up to `MaxPollIntervalMs`). When messages are found, it resets to `MinPollIntervalMs`.

### 2.4 Group by topic and partition key

Messages are grouped by `(TopicName, PartitionKey)`. Each group is processed independently on a worker thread, so a failure in one topic doesn't affect others. Each group starts a fresh in-memory `attempt` counter at 0.

### 2.5 Check circuit breaker

If the circuit breaker is **open** for a topic, the group is skipped entirely. Messages stay in the outbox untouched — the in-memory attempt counter isn't touched and no database write happens. They'll be picked up on the next poll once the circuit closes, and `IOutboxEventHandler.OnPublishFailedAsync` fires with `PublishFailureReason.CircuitOpened`.

This prevents attempt exhaustion during broker outages.

### 2.6 Apply interceptors

If any `IOutboxMessageInterceptor` instances are registered, they run against each message. Interceptors can modify headers, payload, or event type before sending. Transport-level interceptors (`ITransportMessageInterceptor<EventData>`) run in the next step.

### 2.7 Send to Event Hub

The Event Hub transport resolves an `EventHubProducerClient` per topic name from an internal cache (`ConcurrentDictionary<string, EventHubProducerClient>`). Clients are created lazily on first use via the `EventHubClientFactory` delegate—by default this creates a client from the namespace-level connection string + topic name, but it can be replaced with `UseClientFactory()` for custom auth (e.g., `DefaultAzureCredential`). Created clients are reused for subsequent sends to the same topic. It then creates an `EventDataBatch` and adds messages one by one.

**Message construction:**

For each `OutboxMessage`, an `EventData` is created:

- `Body` ← `msg.Payload` (raw bytes)
- `Properties["EventType"]` ← `msg.EventType`
- All `msg.Headers` entries are copied to `Properties`

Transport interceptors (`ITransportMessageInterceptor<EventData>`) can modify the `EventData` before it's added to the batch.

**Batch splitting:**

```
for each message in group:
    eventData = new EventData(msg.Payload)
    copy headers + EventType to eventData.Properties
    run transport interceptors

    if batch.TryAdd(eventData) fails:
        send current batch        ← EventHubProducerClient.SendAsync()
        create new batch
        add eventData to new batch (throw if single message too large)

send final batch                  ← EventHubProducerClient.SendAsync()
```

The `CreateBatchOptions` set:

- `PartitionKey` — the outbox `PartitionKey`, so all messages in a group land on the same Event Hub partition
- `MaximumSizeInBytes` — from `EventHubTransportOptions.MaxBatchSizeBytes` (default 1 MB)

Each `SendAsync` call has a timeout of `SendTimeoutSeconds` (default 15s), reset after each successful sub-batch send.

**Partial send handling:**

If sub-batches 1 and 2 succeed but sub-batch 3 fails, the transport throws a `PartialSendException` containing:

- `SucceededSequenceNumbers` — messages already sent (can't be unsent)
- `FailedSequenceNumbers` — messages that weren't sent

### 2.8 Finalize outcomes

On `SendAsync` success:

```sql
DELETE FROM dbo.Outbox
WHERE SequenceNumber IN (SELECT SequenceNumber FROM @Ids);
```

Circuit breaker records a success. If the delete fails (database error), messages remain in the outbox and will be re-delivered on the next poll (at-least-once guarantee).

On a `SendAsync` exception, the transport's `IsTransient(Exception)` decides what happens next:

**Transient failure** (connection reset, timeout, broker unavailable):

- The circuit breaker records a transient failure. After `CircuitBreakerFailureThreshold` consecutive hits, the circuit opens for `CircuitBreakerOpenDurationSeconds`.
- The in-memory `attempt` counter is **not** touched.
- The retry loop sleeps `min(RetryBackoffBaseMs × 2^(attempt-1), RetryBackoffMaxMs)` milliseconds, then either retries the same group or exits if the circuit has opened.
- No database write happens.

**Non-transient failure** (serialization error, permission denied, message too large):

- `attempt += 1`.
- If `attempt < MaxPublishAttempts`, the loop retries the remaining messages after backoff.
- If `attempt >= MaxPublishAttempts`, the group is dead-lettered inline via `DeadLetterAsync` (see [phase 6](#phase-6-dead-lettering-inline)) and `OnPublishFailedAsync` fires with `PublishFailureReason.RetriesExhausted`.

**Partial send** (Kafka transport only — Event Hub batches are atomic):

- Succeeded messages → delete with the query above.
- Failed messages → `SendAsync` throws `PartialSendException` and the retry loop continues with only the failed subset, classifying the underlying error via `IsTransient`.

### 2.9 No safety net needed

Since there are no per-message leases, cancellation doesn't require cleanup. Partition ownership is the sole isolation mechanism—when the publisher shuts down, `UnregisterPublisherAsync` releases partitions, and unprocessed messages are simply picked up by the next owner.

---

## Phase 3: heartbeat loop

Runs every `HeartbeatIntervalMs` (default 10s). Keeps this publisher's registration alive.

### 3.1 Update heartbeat

```sql
UPDATE dbo.OutboxPublishers
SET    LastHeartbeatUtc = SYSUTCDATETIME()
WHERE  PublisherId = @PublisherId;

UPDATE dbo.OutboxPartitions
SET    GraceExpiresUtc = NULL
WHERE  OwnerPublisherId = @PublisherId
  AND  GraceExpiresUtc IS NOT NULL;
```

Both statements run in a single transaction. The second statement clears any grace period on partitions this publisher owns—proving it's still alive and actively processing.

### 3.2 Update pending count metric

After each heartbeat, the publisher queries the pending message count for observability:

```sql
SELECT COUNT_BIG(*) FROM dbo.Outbox;
```

This is best-effort—failures are logged at `Debug` level and don't affect the heartbeat.

### 3.3 Failure handling

If the heartbeat fails three consecutive times, the loop re-throws the exception. This triggers the restart mechanism—all loops are cancelled via the linked CTS and restarted. The rationale: if you can't heartbeat, other publishers will think you're dead and start claiming your partitions. Restarting ensures a clean slate.

---

## Phase 4: rebalance loop

Runs every `RebalanceIntervalMs` (default 30s). Distributes partitions fairly across all live publishers.

### 4.1 Rebalance query

The entire rebalance runs as a single transaction:

```sql
DECLARE @TotalPartitions   INT;
DECLARE @ActivePublishers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

-- Count total partitions
SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

-- Count live publishers (heartbeat within threshold)
SELECT @ActivePublishers = COUNT(*)
FROM dbo.OutboxPublishers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

-- Fair share = ceil(partitions / publishers)
SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActivePublishers, 0));

-- How many does this publisher own?
SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerPublisherId = @PublisherId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

-- If under fair share, acquire more
IF @ToAcquire > 0
BEGIN
    -- Mark stale publishers' partitions with grace period
    UPDATE dbo.OutboxPartitions
    SET    GraceExpiresUtc = DATEADD(SECOND, @PartitionGracePeriodSeconds, SYSUTCDATETIME())
    WHERE  OwnerPublisherId <> @PublisherId
      AND  OwnerPublisherId IS NOT NULL
      AND  GraceExpiresUtc IS NULL
      AND  OwnerPublisherId NOT IN
           (
               SELECT PublisherId
               FROM   dbo.OutboxPublishers
               WHERE  LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
           );

    -- Claim unowned or grace-expired partitions
    UPDATE op
    SET    OwnerPublisherId = @PublisherId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    FROM   dbo.OutboxPartitions op WITH (UPDLOCK, READPAST)
    WHERE  op.PartitionId IN (
               SELECT TOP (@ToAcquire) PartitionId
               FROM   dbo.OutboxPartitions WITH (UPDLOCK, READPAST)
               WHERE  (OwnerPublisherId IS NULL
                       OR GraceExpiresUtc < SYSUTCDATETIME())
               ORDER BY PartitionId
           );
END;

-- If over fair share, release excess (highest partition IDs first)
SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerPublisherId = @PublisherId;

IF @CurrentlyOwned > @FairShare
BEGIN
    DECLARE @ToRelease INT = @CurrentlyOwned - @FairShare;

    UPDATE op
    SET    OwnerPublisherId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    FROM   dbo.OutboxPartitions op
    WHERE  op.PartitionId IN (
               SELECT TOP (@ToRelease) PartitionId
               FROM   dbo.OutboxPartitions
               WHERE  OwnerPublisherId = @PublisherId
               ORDER BY PartitionId DESC
           );
END;
```

**Parameters:**

- `@PublisherId` — this publisher's ID
- `@HeartbeatTimeoutSeconds` — staleness threshold (default 30)
- `@PartitionGracePeriodSeconds` — safety window before takeover (default 60)

**How it works, step by step:**

1. Calculate **fair share** — `CEIL(total partitions / active publishers)`. With 64 partitions and 2 publishers, each gets 32.
2. If this publisher is **under** its fair share:
   - Mark stale publishers' partitions with a grace expiry (the grace period gives the original owner time to finish in-flight work)
   - Claim partitions that are unowned or past their grace expiry, using `UPDLOCK, READPAST` to avoid contention
3. If this publisher is **over** its fair share (another publisher came online), release excess partitions starting from the highest partition IDs.

### 4.2 Post-rebalance callback

After the rebalance query, the publisher fetches its owned partitions:

```sql
SELECT PartitionId
FROM   dbo.OutboxPartitions
WHERE  OwnerPublisherId = @PublisherId;
```

And fires `IOutboxEventHandler.OnRebalanceAsync` with the result.

---

## Phase 5: orphan sweep loop

Runs every `OrphanSweepIntervalMs` (default 60s). Claims partitions that have no owner—typically left behind when a publisher dies without graceful shutdown.

```sql
DECLARE @TotalPartitions   INT;
DECLARE @ActivePublishers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActivePublishers = COUNT(*)
FROM dbo.OutboxPublishers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActivePublishers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerPublisherId = @PublisherId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

IF @ToAcquire > 0
BEGIN
    UPDATE op
    SET    OwnerPublisherId = @PublisherId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    FROM   dbo.OutboxPartitions op WITH (UPDLOCK, READPAST)
    WHERE  op.PartitionId IN (
               SELECT TOP (@ToAcquire) PartitionId
               FROM   dbo.OutboxPartitions WITH (UPDLOCK, READPAST)
               WHERE  OwnerPublisherId IS NULL
               ORDER BY PartitionId
           );
END;
```

The key difference from rebalance: this only claims `NULL`-owner partitions. It doesn't mark stale partitions or release excess ones—that's the rebalance loop's job.

---

## Phase 6: dead-lettering (inline)

There is no background dead-letter sweep loop. Dead-lettering happens inline from the publish loop (phase 2.8) when the in-memory `attempt` counter hits `MaxPublishAttempts` after a non-transient failure. A single atomic statement moves the rows from `dbo.Outbox` to `dbo.OutboxDeadLetter`:

```sql
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.PayloadContentType,
       deleted.CreatedAtUtc, @AttemptCount,
       deleted.EventDateTimeUtc,
       SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, PayloadContentType,
     CreatedAtUtc, AttemptCount,
     EventDateTimeUtc,
     DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber;
```

**Parameters:**

- `@Ids` — table-valued parameter (`dbo.SequenceNumberList`) containing the exhausted group's sequence numbers
- `@AttemptCount` — the final in-memory attempt count (equals `MaxPublishAttempts` at this point)
- `@LastError` — the message from the last non-transient exception

The call uses `CancellationToken.None` so it completes even during shutdown. After dead-lettering, `IOutboxEventHandler.OnMessageDeadLetteredAsync` fires for each message and `OnPublishFailedAsync` fires for the group with `PublishFailureReason.RetriesExhausted`.

If the publisher crashes mid-retry before dead-lettering runs, the rows stay in the outbox. The next fetch picks them up with a fresh `attempt = 0` budget — retry state never survives a process restart.

---

## Phase 7: shutdown

When the host signals cancellation:

### 7.1 Cancel all loops

The linked `CancellationTokenSource` is cancelled. Each loop catches `OperationCanceledException` and exits cleanly.

### 7.2 Unregister publisher

```sql
UPDATE dbo.OutboxPartitions
SET    OwnerPublisherId = NULL,
       OwnedSinceUtc  = NULL,
       GraceExpiresUtc = NULL
WHERE  OwnerPublisherId = @PublisherId;

DELETE FROM dbo.OutboxPublishers
WHERE  PublisherId = @PublisherId;
```

Both statements run in a single transaction. This releases all owned partitions immediately (no grace period needed—the publisher is done) and removes the publisher registration. Uses `CancellationToken.None` to ensure it completes.

If unregistration fails, it's logged as a warning. The orphan sweep and rebalance loops on other publishers will eventually reclaim the partitions after the heartbeat times out.

---

## Transient error handling

Every SQL Server operation goes through `ExecuteWithRetryAsync`, which retries on transient errors:

- **Max attempts:** 6 (default)
- **Backoff:** exponential with 25% jitter — `1s → 2s → 4s → 8s → 16s → 32s` (base values before jitter)
- **Transient errors:** deadlocks (1205), timeouts (-2), connection failures (-1, 64, 233), TCP errors (10053, 10054, 10060), Azure SQL transient errors (10928, 10929, 40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920), and network-level `IOException`/`SocketException`

Each retry opens a fresh connection from the factory.

---

## Table-valued parameters

SQL Server uses a custom type for passing sequence number arrays:

```sql
CREATE TYPE dbo.SequenceNumberList AS TABLE (SequenceNumber BIGINT NOT NULL PRIMARY KEY);
```

Used by `DeletePublishedAsync` and `DeadLetterAsync` for the `@Ids` parameter. The .NET code creates a `DataTable` with a single `SequenceNumber` column and passes it as a `SqlDbType.Structured` parameter.

---

## Timing diagram

A typical lifecycle with one publisher and 64 partitions:

```
t=0s     RegisterPublisherAsync (MERGE into OutboxPublishers)
         Launch 4 parallel loops
         ├── Publish loop starts polling (100ms intervals)
         ├── Heartbeat loop starts (10s intervals)
         ├── Rebalance loop starts (30s intervals)
         └── Orphan sweep loop starts (60s intervals)

t=0.1s   FetchBatchAsync → 0 messages (no partitions owned yet)
         Poll interval backs off to 200ms

t=30s    RebalanceAsync runs
         - 64 partitions / 1 publisher = 64 fair share
         - Claims all 64 unowned partitions

t=30.1s  FetchBatchAsync → up to 100 messages
         Send to Event Hub → DeletePublishedAsync

t=10s    HeartbeatAsync (updates LastHeartbeatUtc, clears grace periods)
t=20s    HeartbeatAsync
t=30s    HeartbeatAsync
...

t=60s    OrphanSweepAsync (no orphans—all owned)
         RebalanceAsync (no change—still 1 publisher)

--- Publisher B comes online ---

t=90s    RebalanceAsync on Publisher A
         - 64 partitions / 2 publishers = 32 fair share
         - Publisher A owns 64, releases 32 (highest IDs)

         RebalanceAsync on Publisher B
         - Claims 32 unowned partitions

--- Publisher A shuts down ---

t=???    UnregisterPublisherAsync
         - Releases all 32 partitions (OwnerPublisherId = NULL)
         - Deletes from OutboxPublishers

         Publisher B's next OrphanSweepAsync or RebalanceAsync
         - Claims the 32 orphaned partitions
```

---

## Query-to-loop cheat sheet

| Query                                                  | Loop         | Frequency                        | Uses transaction? |
| ------------------------------------------------------ | ------------ | -------------------------------- | ----------------- |
| `MERGE OutboxPublishers`                               | Startup      | Once                             | No                |
| `SELECT COUNT(*) FROM OutboxPartitions`                | Publish      | Cached (60s refresh)             | No                |
| `SELECT TOP ... FROM Outbox` (FetchBatch)              | Publish      | Every poll (100ms–5s)            | No                |
| `DELETE FROM Outbox WHERE SequenceNumber IN ...`       | Publish      | After each successful send       | No                |
| `DELETE Outbox OUTPUT INTO OutboxDeadLetter` (by IDs)  | Publish      | When retries exhausted in-memory | No                |
| `UPDATE OutboxPublishers SET LastHeartbeatUtc`         | Heartbeat    | Every 10s                        | Yes               |
| `SELECT COUNT_BIG(*) FROM Outbox`                      | Heartbeat    | Every 10s                        | No                |
| Rebalance (multi-step)                                 | Rebalance    | Every 30s                        | Yes               |
| Orphan claim (multi-step)                              | Orphan sweep | Every 60s                        | Yes               |
| `UPDATE OutboxPartitions ... DELETE OutboxPublishers`  | Shutdown     | Once                             | Yes               |
