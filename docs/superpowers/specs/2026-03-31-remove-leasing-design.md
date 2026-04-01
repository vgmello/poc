# Remove Leasing — Design Spec

## Problem

The outbox publisher uses per-message leasing (UPDATE to set `leased_until_utc` and `lease_owner`) to claim messages during processing. This UPDATE changes `RowVer`/`xmin`, which breaks the version ceiling filter (`RowVer < MIN_ACTIVE_ROWVERSION()` / `xmin < pg_snapshot_xmin()`) that prevents cross-transaction ordering violations. Leasing is redundant because partition ownership already guarantees single-writer access per partition, and the publish loop is sequential (one poll cycle completes before the next begins).

## Solution

Remove per-message leasing entirely. Change the publish flow from `SELECT+UPDATE(lease) → send → DELETE/UPDATE(release)` to `SELECT → send → DELETE`. The version ceiling filter works cleanly because `RowVer`/`xmin` only changes on INSERT and the rare `retry_count` increment on transport failure.

## Schema

### PostgreSQL — outbox table

```sql
CREATE TABLE outbox
(
    sequence_number      BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    topic_name           VARCHAR(256)   NOT NULL,
    partition_key        VARCHAR(256)   NOT NULL,
    event_type           VARCHAR(256)   NOT NULL,
    headers              VARCHAR(2000)  NULL,
    payload              BYTEA          NOT NULL,
    created_at_utc       TIMESTAMPTZ(3) NOT NULL DEFAULT clock_timestamp(),
    event_datetime_utc   TIMESTAMPTZ(3) NOT NULL,
    event_ordinal        INT            NOT NULL DEFAULT 0,
    payload_content_type VARCHAR(100)   NOT NULL DEFAULT 'application/json',
    retry_count          INT            NOT NULL DEFAULT 0,

    CONSTRAINT pk_outbox PRIMARY KEY (sequence_number)
);
```

Removed: `leased_until_utc`, `lease_owner`.

### SQL Server — Outbox table

```sql
CREATE TABLE dbo.Outbox
(
    SequenceNumber     BIGINT IDENTITY(1,1)  NOT NULL,
    TopicName          NVARCHAR(256)         NOT NULL,
    PartitionKey       NVARCHAR(256)         NOT NULL,
    EventType          NVARCHAR(256)         NOT NULL,
    Headers            NVARCHAR(2000)        NULL,
    Payload            VARBINARY(MAX)        NOT NULL,
    CreatedAtUtc       DATETIME2(3)          NOT NULL  DEFAULT SYSUTCDATETIME(),
    EventDateTimeUtc   DATETIME2(3)          NOT NULL,
    EventOrdinal       INT                   NOT NULL  DEFAULT 0,
    PayloadContentType NVARCHAR(100)         NOT NULL  DEFAULT 'application/json',
    RetryCount         INT                   NOT NULL  DEFAULT 0,
    RowVer             ROWVERSION            NOT NULL,

    CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (SequenceNumber)
);
```

Removed: `LeasedUntilUtc`, `LeaseOwner`. Added: `RowVer ROWVERSION` (for `MIN_ACTIVE_ROWVERSION()` filter).

### Indexes

PostgreSQL:
```sql
CREATE INDEX ix_outbox_pending
ON outbox (event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, retry_count, created_at_utc);
```

SQL Server:
```sql
CREATE NONCLUSTERED INDEX IX_Outbox_Pending
ON dbo.Outbox (EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, RetryCount, CreatedAtUtc);
```

Removed: `ix_outbox_unleased`, `ix_outbox_lease_expiry`, `ix_outbox_sweep` (all referenced lease columns).

### Dead-letter table

Unchanged. No lease columns exist there.

### Other tables

`outbox_publishers`, `outbox_partitions` — unchanged.

## IOutboxStore Interface

```csharp
public interface IOutboxStore
{
    Task<string> RegisterPublisherAsync(CancellationToken ct);
    Task UnregisterPublisherAsync(string publisherId, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize,
        int maxRetryCount, CancellationToken ct);

    Task DeletePublishedAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task IncrementRetryCountAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct);

    Task HeartbeatAsync(string publisherId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct);
    Task RebalanceAsync(string publisherId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct);

    Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct);

    Task<long> GetPendingCountAsync(CancellationToken ct);
}
```

Changes from current interface:
- `LeaseBatchAsync` → `FetchBatchAsync` (removed `leaseDurationSeconds` parameter)
- `DeletePublishedAsync` — removed `publisherId` parameter (no lease_owner check)
- `DeadLetterAsync` — removed `publisherId` parameter
- `ReleaseLeaseAsync` — removed entirely
- `IncrementRetryCountAsync` — new method
- `SweepDeadLettersAsync` — same signature, simplified implementation

## SQL Queries

### FetchBatch (PostgreSQL)

```sql
SELECT o.sequence_number, o.topic_name, o.partition_key, o.event_type,
       o.headers, o.payload, o.payload_content_type,
       o.event_datetime_utc, o.event_ordinal,
       o.retry_count, o.created_at_utc
FROM outbox o
INNER JOIN outbox_partitions op
    ON  op.outbox_table_name = @outbox_table_name
    AND op.owner_publisher_id = @publisher_id
    AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < clock_timestamp())
    AND ((hashtext(o.partition_key) & 2147483647) % @total_partitions) = op.partition_id
WHERE o.retry_count < @max_retry_count
  AND o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
ORDER BY o.event_datetime_utc, o.event_ordinal
LIMIT @batch_size;
```

Plain SELECT — no CTE, no `FOR UPDATE`, no `SKIP LOCKED`. Under PostgreSQL's READ COMMITTED + MVCC, uncommitted rows from other transactions are invisible (no blocking occurs). The version ceiling filter (`xmin`) is the sole mechanism for preventing reads of rows committed while earlier transactions are still in-flight. Partition ownership + sequential polling eliminates the need for row-level locking.

### FetchBatch (SQL Server)

```sql
SELECT TOP (@BatchSize)
    o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType,
    o.Headers, o.Payload, o.PayloadContentType,
    o.EventDateTimeUtc, o.EventOrdinal,
    o.RetryCount, o.CreatedAtUtc
FROM Outbox o WITH (ROWLOCK, READPAST)
INNER JOIN OutboxPartitions op
    ON  op.OutboxTableName = @OutboxTableName
    AND op.OwnerPublisherId = @PublisherId
    AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
    AND (ABS(CAST(CHECKSUM(o.PartitionKey) AS BIGINT)) % @TotalPartitions) = op.PartitionId
WHERE o.RetryCount < @MaxRetryCount
  AND o.RowVer < MIN_ACTIVE_ROWVERSION()
ORDER BY o.EventDateTimeUtc, o.EventOrdinal;
```

No UPDATE. `READPAST` skips locked rows. Version ceiling filter (`RowVer`) prevents reading rows from uncommitted transactions.

### DeletePublished

PostgreSQL:
```sql
DELETE FROM outbox WHERE sequence_number = ANY(@ids);
```

SQL Server:
```sql
DELETE FROM Outbox WHERE SequenceNumber IN (SELECT SequenceNumber FROM @Ids);
```

No `lease_owner` check.

### IncrementRetryCount

PostgreSQL:
```sql
UPDATE outbox SET retry_count = retry_count + 1
WHERE sequence_number = ANY(@ids);
```

SQL Server:
```sql
UPDATE o SET o.RetryCount = o.RetryCount + 1
FROM Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber;
```

### DeadLetter

Same atomic DELETE+INSERT pattern as today, minus the `lease_owner` check.

### SweepDeadLetters

PostgreSQL:
```sql
WITH dead AS (
    DELETE FROM outbox o
    USING (
        SELECT sequence_number FROM outbox
        WHERE retry_count >= @max_retry_count
        FOR UPDATE SKIP LOCKED
    ) d
    WHERE o.sequence_number = d.sequence_number
    RETURNING o.*
)
INSERT INTO outbox_dead_letter (...) SELECT ... FROM dead;
```

SQL Server:
```sql
DELETE o
OUTPUT deleted.* INTO OutboxDeadLetter(...)
FROM Outbox o WITH (ROWLOCK, READPAST)
WHERE o.RetryCount >= @MaxRetryCount;
```

No lease-based conditions.

### GetPendingCount

PostgreSQL:
```sql
SELECT COUNT(*) FROM outbox;
```

SQL Server:
```sql
SELECT COUNT_BIG(*) FROM Outbox;
```

No lease filter — all rows in the table are pending.

## OutboxPublisherService Changes

### PublishLoopAsync

1. Call `FetchBatchAsync` instead of `LeaseBatchAsync` (no lease duration parameter).
2. Remove the `unprocessedSequences` ConcurrentDictionary and its `finally` block. No leases to release on shutdown or cancellation.
3. Poison messages (retry_count >= MaxRetryCount) are still separated and dead-lettered at the start of each cycle.

### ProcessGroupsAsync

**On success:**
- `DeletePublishedAsync(sequenceNumbers, ct)` — no publisherId.
- If delete fails: do nothing. Messages stay in the table. Next poll picks them up. At-least-once delivery handles duplicates. Do NOT increment retry count (transport succeeded).

**On transport failure:**
- `IncrementRetryCountAsync(sequenceNumbers, ct)` — replaces `ReleaseLeaseAsync(incrementRetry: true)`.
- Circuit breaker records failure.

**On partial send (PartialSendException):**
- Succeeded messages: `DeletePublishedAsync`. If delete fails, do nothing.
- Failed messages: `IncrementRetryCountAsync`.

**On circuit breaker open:**
- Do nothing. Messages stay as-is. Next poll after circuit closes picks them up.

### Graceful Shutdown

- Remove all `ReleaseLeaseAsync` calls from finally blocks.
- Keep `UnregisterPublisherAsync` (releases partition ownership).
- Messages in-flight at shutdown remain in the table. After partition reassignment, another publisher picks them up.

## OutboxPublisherOptions Changes

Remove `LeaseDurationSeconds`. No longer needed.

The validation constraint `LeaseDurationSeconds < PartitionGracePeriodSeconds` is removed. `PartitionGracePeriodSeconds` is still needed for partition reassignment safety.

## OutboxMessage Model

Remove `LeaseOwner` and `LeasedUntilUtc` if present. The record becomes:

```csharp
public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    Dictionary<string, string>? Headers,
    byte[] Payload,
    string PayloadContentType,
    DateTimeOffset EventDateTimeUtc,
    int EventOrdinal,
    int RetryCount,
    DateTimeOffset CreatedAtUtc);
```

## Version Ceiling Interaction

Without lease/release UPDATEs, `RowVer`/`xmin` only changes on:
- INSERT (initial value, used by the version ceiling filter)
- `IncrementRetryCountAsync` (transport failure only)

The `retry_count` increment UPDATE changes `RowVer`/`xmin`, which may cause the version ceiling to temporarily block the message on the next poll if a concurrent publisher loop (heartbeat, rebalance) has an active transaction. This delay is at most one poll cycle (50-500ms) and only occurs during failure scenarios. No bypass logic is needed.

## What's NOT Changing

- Partition ownership model (outbox_partitions, rebalance, orphan sweep, heartbeat)
- Transport interface (IOutboxTransport)
- Circuit breaker logic
- Dead-letter table schema
- Dead-letter manager (replay, purge)
- Event handler interface (IOutboxEventHandler)
- Health check
- PublishThreadCount worker fan-out within the publish loop
