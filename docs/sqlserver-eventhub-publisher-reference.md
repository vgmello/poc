# SQL Server + Event Hub publisher reference

Step-by-step breakdown of everything the outbox publisher does when backed by SQL Server and Azure Event Hubs—from startup to shutdown, with every query it runs.

## Configuration defaults

Before diving in, here are the options that drive the timings referenced throughout:

| Option | Default | Used by |
|--------|---------|---------|
| `PublisherName` | `outbox-publisher` | Producer ID generation |
| `BatchSize` | 100 | Lease query `TOP` |
| `LeaseDurationSeconds` | 45 | Lease expiry on messages |
| `MaxRetryCount` | 5 | Poison threshold |
| `MinPollIntervalMs` | 100 | Publish loop (busy) |
| `MaxPollIntervalMs` | 5000 | Publish loop (idle) |
| `HeartbeatIntervalMs` | 10,000 | Heartbeat loop delay |
| `HeartbeatTimeoutSeconds` | 30 | Staleness detection |
| `PartitionGracePeriodSeconds` | 60 | Partition takeover safety window |
| `RebalanceIntervalMs` | 30,000 | Rebalance loop delay |
| `OrphanSweepIntervalMs` | 60,000 | Orphan sweep loop delay |
| `DeadLetterSweepIntervalMs` | 60,000 | Dead-letter sweep loop delay |
| `CircuitBreakerFailureThreshold` | 3 | Consecutive failures to trip |
| `CircuitBreakerOpenDurationSeconds` | 30 | How long the circuit stays open |

Event Hub transport options:

| Option | Default | Purpose |
|--------|---------|---------|
| `ConnectionString` | *(required)* | Namespace-level Event Hub connection string |
| `MaxBatchSizeBytes` | 1,048,576 (1 MB) | Max `EventDataBatch` size |
| `SendTimeoutSeconds` | 15 | Per-batch send timeout |

SQL Server store options:

| Option | Default | Purpose |
|--------|---------|---------|
| `SchemaName` | `dbo` | SQL schema for all tables |
| `TablePrefix` | *(empty)* | Prefix prepended to table names |
| `CommandTimeoutSeconds` | 30 | SQL command timeout |
| `TransientRetryMaxAttempts` | 6 | Retry count for transient SQL errors |
| `TransientRetryBackoffMs` | 1,000 | Base backoff between retries |

All table names below assume the defaults (`dbo.Outbox`, `dbo.OutboxProducers`, etc.). If you set `SchemaName` or `TablePrefix`, the actual names change accordingly.

---

## Phase 1: startup and registration

When the `OutboxPublisherService` starts, it generates a producer ID and registers itself in SQL Server.

### 1.1 Generate producer ID

Format: `{PublisherName}-{Guid:N}`

Example: `outbox-publisher-a1b2c3d4e5f6789012345678abcdef01`

### 1.2 Register producer

The publisher inserts (or updates) its row in the producers table using a `MERGE` with `HOLDLOCK` to handle restarts safely:

```sql
MERGE dbo.OutboxProducers WITH (HOLDLOCK) AS target
USING (SELECT @ProducerId AS ProducerId, @HostName AS HostName) AS source
    ON target.ProducerId = source.ProducerId
WHEN MATCHED THEN
    UPDATE SET LastHeartbeatUtc = SYSUTCDATETIME(),
               HostName         = source.HostName
WHEN NOT MATCHED THEN
    INSERT (ProducerId, RegisteredAtUtc, LastHeartbeatUtc, HostName)
    VALUES (source.ProducerId, SYSUTCDATETIME(), SYSUTCDATETIME(), source.HostName);
```

**Parameters:**
- `@ProducerId` — the generated producer ID
- `@HostName` — `Environment.MachineName`

If registration fails (database unavailable, network issues), the publisher retries with exponential backoff: `2s → 4s → 8s → ...` capped at 60s. It keeps retrying until it succeeds or the host shuts down.

### 1.3 Initialize circuit breaker

A `TopicCircuitBreaker` is created in memory (not persisted). It tracks failures per topic name and transitions through three states: **Closed → Open → HalfOpen → Closed**.

### 1.4 Launch parallel loops

After successful registration, the publisher creates a linked `CancellationTokenSource` and starts five parallel `Task`s. If any loop exits—whether from a crash or an unexpected return—all loops are cancelled and the entire set restarts with exponential backoff (`2s → 4s → 8s → 16s → 32s`). After five consecutive restarts without 30 seconds of healthy operation, the publisher stops the host application.

---

## Phase 2: publish loop

The core loop—leases messages, sends them to Event Hub, and finalizes the outcome. Runs continuously until cancelled.

### 2.1 Get partition count (cached)

Before leasing, the publisher needs the total partition count. It's cached for 60 seconds to avoid hitting SQL Server on every poll:

```sql
SELECT COUNT(*) FROM dbo.OutboxPartitions;
```

If the count is 0, the publisher skips leasing and waits.

### 2.2 Lease a batch

This is the most complex query. It atomically selects and locks messages in a single `UPDATE...OUTPUT` statement:

```sql
WITH Batch AS
(
    SELECT TOP (@BatchSize)
        o.SequenceNumber, o.TopicName, o.PartitionKey,
        o.EventType, o.Headers, o.Payload,
        o.PayloadContentType, o.EventDateTimeUtc, o.EventOrdinal,
        o.LeasedUntilUtc, o.LeaseOwner, o.RetryCount, o.CreatedAtUtc
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    INNER JOIN dbo.OutboxPartitions op
        ON  op.OwnerProducerId = @PublisherId
        AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
        AND (ABS(CAST(CHECKSUM(o.PartitionKey) AS BIGINT)) % @TotalPartitions) = op.PartitionId
    WHERE (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME())
      AND o.RetryCount < @MaxRetryCount
    ORDER BY o.EventDateTimeUtc, o.EventOrdinal
)
UPDATE Batch
SET    LeasedUntilUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()),
       LeaseOwner     = @PublisherId,
       RetryCount     = CASE WHEN LeasedUntilUtc IS NOT NULL
                              THEN RetryCount + 1
                              ELSE RetryCount END
OUTPUT inserted.SequenceNumber, inserted.TopicName, inserted.PartitionKey,
       inserted.EventType, inserted.Headers, inserted.Payload,
       inserted.PayloadContentType, inserted.EventDateTimeUtc,
       inserted.EventOrdinal, inserted.RetryCount, inserted.CreatedAtUtc;
```

**Parameters:**
- `@BatchSize` — max messages to lease (default 100)
- `@LeaseDurationSeconds` — how long the lease lasts (default 45)
- `@PublisherId` — this producer's ID
- `@TotalPartitions` — cached partition count
- `@MaxRetryCount` — poison threshold (default 5)

**What the filters do:**

| Filter | Purpose |
|--------|---------|
| `ROWLOCK, READPAST` | Row-level locking; skip rows locked by other transactions |
| `op.OwnerProducerId = @PublisherId` | Only lease from partitions this producer owns |
| `op.GraceExpiresUtc IS NULL OR < NOW` | Don't lease from partitions still in grace period |
| `ABS(CHECKSUM(PartitionKey)) % Total = PartitionId` | Hash-based partition assignment |
| `LeasedUntilUtc IS NULL OR < NOW` | Only unleased or expired-lease messages |
| `RetryCount < @MaxRetryCount` | Skip poison messages (handled separately) |
| `ORDER BY EventDateTimeUtc, EventOrdinal` | Strict ordering within a partition key |

**Retry count logic:** The retry count is only incremented if the message was previously leased (`LeasedUntilUtc IS NOT NULL`). Fresh messages get their first lease without a retry bump.

### 2.3 Adaptive polling

If the batch is empty, the poll interval doubles (up to `MaxPollIntervalMs`). When messages are found, it resets to `MinPollIntervalMs`.

### 2.4 Separate poison messages

Messages where `RetryCount >= MaxRetryCount` are split from the batch and dead-lettered immediately (see [phase 7](#phase-7-dead-lettering)).

### 2.5 Group by topic and partition key

Healthy messages are grouped by `(TopicName, PartitionKey)`. Each group is processed independently so a failure in one topic doesn't affect others.

### 2.6 Check circuit breaker

If the circuit breaker is **open** for a topic, messages are released without incrementing the retry count:

```sql
UPDATE o
SET    o.LeasedUntilUtc = NULL,
       o.LeaseOwner     = NULL
FROM   dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;
```

This prevents retry-count exhaustion during broker outages. The messages become available for the next poll once the circuit closes.

### 2.7 Apply interceptors

If any `IOutboxMessageInterceptor` instances are registered, they run against each message. Interceptors can modify headers, payload, or event type before sending. Transport-level interceptors (`ITransportMessageInterceptor<EventData>`) run in the next step.

### 2.8 Send to Event Hub

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

### 2.9 Finalize outcomes

Three possible outcomes:

**Success—all messages sent:**

Delete from the outbox table and record circuit breaker success:

```sql
DELETE o
FROM   dbo.Outbox o
INNER JOIN @PublishedIds p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;
```

The `LeaseOwner` check prevents a zombie publisher (one that lost its lease) from deleting messages now owned by another publisher.

If the delete fails (database error), messages are released without retry increment since the transport already succeeded—they'll be re-delivered (at-least-once guarantee):

```sql
UPDATE o
SET    o.LeasedUntilUtc = NULL,
       o.LeaseOwner     = NULL
FROM   dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;
```

**Partial send:**

- Succeeded messages → delete (same query as success)
- Failed messages → release with retry increment:

```sql
UPDATE o
SET    o.LeasedUntilUtc = NULL,
       o.LeaseOwner     = NULL,
       o.RetryCount     = o.RetryCount + 1
FROM   dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;
```

Circuit breaker records a failure.

**Full failure:**

All messages are released with retry increment (same query as failed messages above). Circuit breaker records a failure.

### 2.10 Safety net for cancellation

If the loop is cancelled mid-flight (shutdown), a `finally` block releases any leased messages that weren't finalized—without incrementing retry count, using `CancellationToken.None` to ensure the release completes even during shutdown.

---

## Phase 3: heartbeat loop

Runs every `HeartbeatIntervalMs` (default 10s). Keeps this producer's registration alive.

### 3.1 Update heartbeat

```sql
UPDATE dbo.OutboxProducers
SET    LastHeartbeatUtc = SYSUTCDATETIME()
WHERE  ProducerId = @ProducerId;

UPDATE dbo.OutboxPartitions
SET    GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId
  AND  GraceExpiresUtc IS NOT NULL;
```

Both statements run in a single transaction. The second statement clears any grace period on partitions this producer owns—proving it's still alive and actively processing.

### 3.2 Update pending count metric

After each heartbeat, the publisher queries the pending message count for observability:

```sql
SELECT COUNT_BIG(*) FROM dbo.Outbox
WHERE  LeasedUntilUtc IS NULL OR LeasedUntilUtc < SYSUTCDATETIME();
```

This is best-effort—failures are logged at `Debug` level and don't affect the heartbeat.

### 3.3 Failure handling

If the heartbeat fails three consecutive times, the loop exits. This triggers the restart mechanism—all loops are cancelled and restarted. The rationale: if you can't heartbeat, other producers will think you're dead and start claiming your partitions. Restarting ensures a clean slate.

---

## Phase 4: rebalance loop

Runs every `RebalanceIntervalMs` (default 30s). Distributes partitions fairly across all live producers.

### 4.1 Rebalance query

The entire rebalance runs as a single transaction:

```sql
DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

-- Count total partitions
SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

-- Count live producers (heartbeat within threshold)
SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

-- Fair share = ceil(partitions / producers)
SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

-- How many does this producer own?
SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

-- If under fair share, acquire more
IF @ToAcquire > 0
BEGIN
    -- Mark stale producers' partitions with grace period
    UPDATE dbo.OutboxPartitions
    SET    GraceExpiresUtc = DATEADD(SECOND, @PartitionGracePeriodSeconds, SYSUTCDATETIME())
    WHERE  OwnerProducerId <> @ProducerId
      AND  OwnerProducerId IS NOT NULL
      AND  GraceExpiresUtc IS NULL
      AND  OwnerProducerId NOT IN
           (
               SELECT ProducerId
               FROM   dbo.OutboxProducers
               WHERE  LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
           );

    -- Claim unowned or grace-expired partitions
    UPDATE op
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    FROM   dbo.OutboxPartitions op WITH (UPDLOCK, READPAST)
    WHERE  op.PartitionId IN (
               SELECT TOP (@ToAcquire) PartitionId
               FROM   dbo.OutboxPartitions WITH (UPDLOCK, READPAST)
               WHERE  (OwnerProducerId IS NULL
                       OR GraceExpiresUtc < SYSUTCDATETIME())
               ORDER BY PartitionId
           );
END;

-- If over fair share, release excess (highest partition IDs first)
SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

IF @CurrentlyOwned > @FairShare
BEGIN
    DECLARE @ToRelease INT = @CurrentlyOwned - @FairShare;

    UPDATE op
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    FROM   dbo.OutboxPartitions op
    WHERE  op.PartitionId IN (
               SELECT TOP (@ToRelease) PartitionId
               FROM   dbo.OutboxPartitions
               WHERE  OwnerProducerId = @ProducerId
               ORDER BY PartitionId DESC
           );
END;
```

**Parameters:**
- `@ProducerId` — this producer's ID
- `@HeartbeatTimeoutSeconds` — staleness threshold (default 30)
- `@PartitionGracePeriodSeconds` — safety window before takeover (default 60)

**How it works, step by step:**

1. Calculate **fair share** — `CEIL(total partitions / active producers)`. With 32 partitions and 2 producers, each gets 16.
2. If this producer is **under** its fair share:
   - Mark stale producers' partitions with a grace expiry (the grace period gives the original owner time to finish in-flight work)
   - Claim partitions that are unowned or past their grace expiry, using `UPDLOCK, READPAST` to avoid contention
3. If this producer is **over** its fair share (another producer came online), release excess partitions starting from the highest partition IDs.

### 4.2 Post-rebalance callback

After the rebalance query, the publisher fetches its owned partitions:

```sql
SELECT PartitionId
FROM   dbo.OutboxPartitions
WHERE  OwnerProducerId = @ProducerId;
```

And fires `IOutboxEventHandler.OnRebalanceAsync` with the result.

---

## Phase 5: orphan sweep loop

Runs every `OrphanSweepIntervalMs` (default 60s). Claims partitions that have no owner—typically left behind when a producer dies without graceful shutdown.

```sql
DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

IF @ToAcquire > 0
BEGIN
    UPDATE op
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    FROM   dbo.OutboxPartitions op WITH (UPDLOCK, READPAST)
    WHERE  op.PartitionId IN (
               SELECT TOP (@ToAcquire) PartitionId
               FROM   dbo.OutboxPartitions WITH (UPDLOCK, READPAST)
               WHERE  OwnerProducerId IS NULL
               ORDER BY PartitionId
           );
END;
```

The key difference from rebalance: this only claims `NULL`-owner partitions. It doesn't mark stale partitions or release excess ones—that's the rebalance loop's job.

---

## Phase 6: dead-letter sweep loop

Runs every `DeadLetterSweepIntervalMs` (default 60s). A background safety net that catches poison messages the publish loop's inline check might have missed (e.g., if `DeadLetterAsync` itself failed earlier).

```sql
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.PayloadContentType,
       deleted.CreatedAtUtc, deleted.RetryCount,
       deleted.EventDateTimeUtc, deleted.EventOrdinal,
       SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, PayloadContentType,
     CreatedAtUtc, RetryCount,
     EventDateTimeUtc, EventOrdinal,
     DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
WHERE o.RetryCount >= @MaxRetryCount
  AND (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME())
  AND (o.LeaseOwner IS NULL
       OR o.LeasedUntilUtc < DATEADD(SECOND, -@LeaseDurationSeconds, SYSUTCDATETIME())
       OR o.LeaseOwner NOT IN (
           SELECT ProducerId
           FROM dbo.OutboxProducers
           WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
       ));
```

**Parameters:**
- `@MaxRetryCount` — poison threshold (default 5)
- `@HeartbeatTimeoutSeconds` — staleness threshold (default 30)
- `@LeaseDurationSeconds` — lease duration (default 45)
- `@LastError` — always `"Max retry count exceeded (background sweep)"`

**Three conditions for sweep eligibility** (must meet all):

1. `RetryCount >= MaxRetryCount` — actually a poison message
2. Not currently leased (lease expired or null)
3. **And** one of:
   - `LeaseOwner IS NULL` — explicitly released
   - Lease has been expired for longer than `LeaseDurationSeconds` — owner had time to clean up but didn't
   - Owner is a dead producer (stale heartbeat)

The `DELETE...OUTPUT INTO` is atomic—the message is moved from `Outbox` to `OutboxDeadLetter` in a single statement.

---

## Phase 7: dead-lettering (inline)

During the publish loop (phase 2), messages with `RetryCount >= MaxRetryCount` are dead-lettered immediately, before any transport work:

```sql
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.PayloadContentType,
       deleted.CreatedAtUtc, deleted.RetryCount,
       deleted.EventDateTimeUtc, deleted.EventOrdinal,
       SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, PayloadContentType,
     CreatedAtUtc, RetryCount,
     EventDateTimeUtc, EventOrdinal,
     DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE o.LeaseOwner = @PublisherId;
```

**Parameters:**
- `@PublisherId` — this producer's ID
- `@Ids` — table-valued parameter (`dbo.SequenceNumberList`) containing the poison message sequence numbers
- `@LastError` — `"Max retry count exceeded"`

Uses `CancellationToken.None`—this must complete even during shutdown. After dead-lettering, `IOutboxEventHandler.OnMessageDeadLetteredAsync` fires for each message.

---

## Phase 8: shutdown

When the host signals cancellation:

### 8.1 Cancel all loops

The linked `CancellationTokenSource` is cancelled. Each loop catches `OperationCanceledException` and exits cleanly. The publish loop's `finally` block releases any in-flight leases without retry increment.

### 8.2 Unregister producer

```sql
UPDATE dbo.OutboxPartitions
SET    OwnerProducerId = NULL,
       OwnedSinceUtc  = NULL,
       GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId;

DELETE FROM dbo.OutboxProducers
WHERE  ProducerId = @ProducerId;
```

Both statements run in a single transaction. This releases all owned partitions immediately (no grace period needed—the producer is done) and removes the producer registration. Uses `CancellationToken.None` to ensure it completes.

If unregistration fails, it's logged as a warning. The orphan sweep and rebalance loops on other producers will eventually reclaim the partitions after the heartbeat times out.

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

Used by `DeletePublishedAsync`, `ReleaseLeaseAsync`, and `DeadLetterAsync` for the `@PublishedIds` / `@Ids` parameters. The .NET code creates a `DataTable` with a single `SequenceNumber` column and passes it as a `SqlDbType.Structured` parameter.

---

## Timing diagram

A typical lifecycle with one publisher and 32 partitions:

```
t=0s     RegisterProducerAsync (MERGE into OutboxProducers)
         Launch 5 parallel loops
         ├── Publish loop starts polling (100ms intervals)
         ├── Heartbeat loop starts (10s intervals)
         ├── Rebalance loop starts (30s intervals)
         ├── Orphan sweep loop starts (60s intervals)
         └── Dead-letter sweep loop starts (60s intervals)

t=0.1s   LeaseBatchAsync → 0 messages (no partitions owned yet)
         Poll interval backs off to 200ms

t=30s    RebalanceAsync runs
         - 32 partitions / 1 producer = 32 fair share
         - Claims all 32 unowned partitions

t=30.1s  LeaseBatchAsync → up to 100 messages
         Send to Event Hub → DeletePublishedAsync

t=10s    HeartbeatAsync (updates LastHeartbeatUtc, clears grace periods)
t=20s    HeartbeatAsync
t=30s    HeartbeatAsync
...

t=60s    OrphanSweepAsync (no orphans—all owned)
         DeadLetterSweepAsync (sweeps any missed poison messages)
         RebalanceAsync (no change—still 1 producer)

--- Publisher B comes online ---

t=90s    RebalanceAsync on Publisher A
         - 32 partitions / 2 producers = 16 fair share
         - Publisher A owns 32, releases 16 (highest IDs)

         RebalanceAsync on Publisher B
         - Claims 16 unowned partitions

--- Publisher A shuts down ---

t=???    UnregisterProducerAsync
         - Releases all 16 partitions (OwnerProducerId = NULL)
         - Deletes from OutboxProducers

         Publisher B's next OrphanSweepAsync or RebalanceAsync
         - Claims the 16 orphaned partitions
```

---

## Query-to-loop cheat sheet

| Query | Loop | Frequency | Uses transaction? |
|-------|------|-----------|-------------------|
| `MERGE OutboxProducers` | Startup | Once | No |
| `SELECT COUNT(*) FROM OutboxPartitions` | Publish | Cached (60s refresh) | No |
| `UPDATE Outbox SET LeasedUntilUtc...` (CTE batch) | Publish | Every poll (100ms–5s) | No (single statement) |
| `DELETE Outbox INNER JOIN @PublishedIds` | Publish | After each successful send | No |
| `UPDATE Outbox SET LeasedUntilUtc = NULL` | Publish | On failure/circuit-open/cancellation | No |
| `DELETE Outbox OUTPUT INTO OutboxDeadLetter` (by IDs) | Publish | When poison messages found | No |
| `UPDATE OutboxProducers SET LastHeartbeatUtc` | Heartbeat | Every 10s | Yes |
| `SELECT COUNT_BIG(*) FROM Outbox` | Heartbeat | Every 10s | No |
| Rebalance (multi-step) | Rebalance | Every 30s | Yes |
| Orphan claim (multi-step) | Orphan sweep | Every 60s | Yes |
| `DELETE Outbox OUTPUT INTO OutboxDeadLetter` (by threshold) | Dead-letter sweep | Every 60s | No (single statement) |
| `UPDATE OutboxPartitions ... DELETE OutboxProducers` | Shutdown | Once | Yes |
