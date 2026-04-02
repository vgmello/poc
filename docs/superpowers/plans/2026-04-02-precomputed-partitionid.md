# Precomputed PartitionId Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persisted computed `PartitionId` column to the SQL Server Outbox table so FetchBatch uses an Index Seek instead of a full table scan, reducing poll latency from ~130ms to ~7ms.

**Architecture:** Add `PartitionId AS (ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % 128) PERSISTED` to `dbo.Outbox`. Rewrite the FetchBatch query to use `WHERE PartitionId IN (owned partitions)` instead of a JOIN with per-row CHECKSUM. Create a covering index keyed on `(PartitionId, EventDateTimeUtc, EventOrdinal)`. Increase default partitions from 64 to 128. Update all documentation to reflect the change and the partition count safety constraints.

**Tech Stack:** SQL Server, Dapper, xUnit integration tests

---

## File Structure

```
Modified:
  src/Outbox.SqlServer/db_scripts/install.sql          — computed column, new index, 128 partitions
  src/Outbox.SqlServer/SqlServerQueries.cs              — FetchBatch query rewrite
  src/Outbox.SqlServer/SqlServerOutboxStore.cs          — remove TotalPartitions param from FetchBatch
  docs/known-limitations.md                             — partition count asymmetry
  docs/production-runbook.md                            — partition count change procedures (both stores)
  docs/sqlserver-eventhub-publisher-reference.md        — updated query, filter table, phase 2.2
  docs/architecture.md                                  — precomputed column note, partition count
  docs/publisher-flow.md                                — partition hashing section update
```

---

### Task 1: Update SQL Server Install Script

**Files:**
- Modify: `src/Outbox.SqlServer/db_scripts/install.sql`

- [ ] **Step 1: Add PartitionId computed column to CREATE TABLE**

In `install.sql`, inside the `CREATE TABLE dbo.Outbox` block (lines 15-31), add the computed column after the `RowVersion` line and before the PK constraint:

```sql
        PartitionId      AS (ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % 128) PERSISTED,
```

The full column list becomes:

```sql
    CREATE TABLE dbo.Outbox
    (
        SequenceNumber   BIGINT IDENTITY(1,1)  NOT NULL,
        TopicName        NVARCHAR(256)         NOT NULL,
        PartitionKey     NVARCHAR(256)         NOT NULL,
        EventType        NVARCHAR(256)         NOT NULL,
        Headers          NVARCHAR(2000)        NULL,
        Payload          VARBINARY(MAX)        NOT NULL,
        CreatedAtUtc     DATETIME2(3)          NOT NULL  DEFAULT SYSUTCDATETIME(),
        EventDateTimeUtc DATETIME2(3)          NOT NULL,
        EventOrdinal     INT                   NOT NULL  DEFAULT 0,
        PayloadContentType NVARCHAR(100)       NOT NULL  DEFAULT 'application/json',
        RetryCount       INT                   NOT NULL  DEFAULT 0,
        RowVersion       ROWVERSION            NOT NULL,
        PartitionId      AS (ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % 128) PERSISTED,

        CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (SequenceNumber)
    );
```

- [ ] **Step 2: Update the index to key on PartitionId**

Replace the index definition (lines 115-121) with:

```sql
-- Pending rows: seek by partition, then ordered by causal time (fully covering)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Outbox') AND name = N'IX_Outbox_Pending')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Outbox_Pending
    ON dbo.Outbox (PartitionId, EventDateTimeUtc, EventOrdinal)
    INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, RetryCount, CreatedAtUtc, RowVersion);
END;
```

- [ ] **Step 3: Update partition seed from 64 to 128**

Replace Section 5 (lines 159-173) with:

```sql
-- =============================================================================
-- SECTION 5: SEED DEFAULT PARTITIONS (128 partitions)
-- =============================================================================

DECLARE @i INT = 0;
WHILE @i < 128
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.OutboxPartitions WHERE OutboxTableName = N'Outbox' AND PartitionId = @i)
    BEGIN
        INSERT INTO dbo.OutboxPartitions (OutboxTableName, PartitionId, OwnerPublisherId, OwnedSinceUtc, GraceExpiresUtc)
        VALUES (N'Outbox', @i, NULL, NULL, NULL);
    END;
    SET @i = @i + 1;
END;
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/Outbox.SqlServer/`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.SqlServer/db_scripts/install.sql
git commit -m "feat(sqlserver): add precomputed PartitionId column, new index, 128 partitions"
```

---

### Task 2: Rewrite FetchBatch Query

**Files:**
- Modify: `src/Outbox.SqlServer/SqlServerQueries.cs:69-84`

- [ ] **Step 1: Replace FetchBatch query**

In `SqlServerQueries.cs`, replace the FetchBatch assignment (lines 69-84) with:

```csharp
        FetchBatch = $"""
                     SELECT TOP (@BatchSize)
                         o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType,
                         o.Headers, o.Payload, o.PayloadContentType,
                         o.EventDateTimeUtc, o.EventOrdinal,
                         o.RetryCount, o.CreatedAtUtc
                     FROM {outboxTable} o
                     WHERE o.PartitionId IN (
                         SELECT op.PartitionId
                         FROM {partitionsTable} op
                         WHERE op.OutboxTableName = @OutboxTableName
                           AND op.OwnerPublisherId = @PublisherId
                           AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
                     )
                       AND o.RetryCount < @MaxRetryCount
                       AND o.RowVersion < MIN_ACTIVE_ROWVERSION()
                     ORDER BY o.EventDateTimeUtc, o.EventOrdinal;
                     """;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Outbox.SqlServer/`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.SqlServer/SqlServerQueries.cs
git commit -m "feat(sqlserver): rewrite FetchBatch to use precomputed PartitionId"
```

---

### Task 3: Remove TotalPartitions from FetchBatchAsync

**Files:**
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs:79-96`

- [ ] **Step 1: Remove TotalPartitions from FetchBatchAsync**

Replace the method (lines 79-96) with:

```csharp
    public async Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize,
        int maxRetryCount, CancellationToken ct)
    {
        var rows = await _db.QueryAsync<OutboxMessage>(_queries.FetchBatch,
            new
            {
                BatchSize = batchSize,
                PublisherId = publisherId,
                MaxRetryCount = maxRetryCount,
                OutboxTableName = _options.GetOutboxTableName()
            }, ct).ConfigureAwait(false);

        return rows.AsList();
    }
```

Note: The `GetCachedPartitionCountAsync` call and `totalPartitions == 0` guard are removed from FetchBatch. The partition count is still used by the rebalance/orphan sweep queries (they call `GetTotalPartitionsAsync` separately via their own SQL). The `GetCachedPartitionCountAsync` method and `GetTotalPartitionsAsync` method must remain in the class — they're still called by the `OutboxPublisherService` for worker index computation.

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Outbox.SqlServer/`
Expected: Build succeeded.

- [ ] **Step 3: Run existing unit tests**

Run: `dotnet test tests/Outbox.Core.Tests/`
Expected: All tests pass. (Unit tests mock `IOutboxStore`, so the interface hasn't changed.)

- [ ] **Step 4: Run existing SQL Server integration tests**

Run: `dotnet test tests/Outbox.IntegrationTests/ --filter "FullyQualifiedName~SqlServer"`
Expected: All SQL Server integration tests pass. The schema is re-created from `install.sql` which now includes the computed column.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.SqlServer/SqlServerOutboxStore.cs
git commit -m "feat(sqlserver): remove TotalPartitions param from FetchBatch"
```

---

### Task 4: Update Documentation

**Files:**
- Modify: `docs/known-limitations.md`
- Modify: `docs/production-runbook.md`
- Modify: `docs/sqlserver-eventhub-publisher-reference.md`
- Modify: `docs/architecture.md`
- Modify: `docs/publisher-flow.md`

- [ ] **Step 1: Add partition count asymmetry to known-limitations.md**

At the end of the "Design Decisions" section in `docs/known-limitations.md`, add:

```markdown
### Partition count changes require publisher downtime (both stores)

Changing the partition count (the modulus in `hash(partition_key) % total_partitions`) while publishers are running corrupts per-key message ordering. When the modulus changes, the same `partition_key` maps to a different `partition_id`, breaking the single-writer-per-partition guarantee. Two publishers can then process the same partition key simultaneously.

**SQL Server:** The modulus is baked into a persisted computed column (`PartitionId = ABS(CHECKSUM(PartitionKey)) % 128`). Changing it requires `ALTER TABLE` to drop and recreate the column, an index rebuild, and reseeding the partitions table. See `docs/production-runbook.md` for the procedure.

**PostgreSQL:** The modulus is computed at query time from the row count of `outbox_partitions`. Changing it requires only INSERT/DELETE on the partitions table (no schema change), but **all publishers must still be stopped** to prevent ordering corruption. The simpler schema change does not mean it's safe to do online.

This asymmetry is intentional: SQL Server's precomputed column enables Index Seek (7ms poll latency) instead of the full table scan (130ms) that runtime hash computation would require. PostgreSQL's query optimizer handles the runtime hash efficiently (3-5ms), so the precomputed column is not needed.
```

- [ ] **Step 2: Add partition count change procedures to production-runbook.md**

Before the "Emergency Procedures" section in `docs/production-runbook.md`, add:

```markdown
### Changing Partition Count

**CRITICAL: All publishers must be stopped before changing the partition count on either store.** Changing the hash modulus while publishers are running corrupts per-key ordering — the same partition_key maps to a different partition_id, and two publishers can process the same key simultaneously.

#### SQL Server Procedure

```sql
-- 1. Stop all publishers first!

-- 2. Drop the index (depends on the computed column)
DROP INDEX IX_Outbox_Pending ON dbo.Outbox;

-- 3. Drop and recreate the computed column with new modulus
ALTER TABLE dbo.Outbox DROP COLUMN PartitionId;
ALTER TABLE dbo.Outbox ADD PartitionId AS
    (ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % <NEW_COUNT>) PERSISTED;

-- 4. Recreate the index
CREATE NONCLUSTERED INDEX IX_Outbox_Pending
ON dbo.Outbox (PartitionId, EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload,
         PayloadContentType, RetryCount, CreatedAtUtc, RowVersion);

-- 5. Reseed partitions
DELETE FROM dbo.OutboxPartitions WHERE OutboxTableName = 'Outbox';
DECLARE @i INT = 0;
WHILE @i < <NEW_COUNT>
BEGIN
    INSERT INTO dbo.OutboxPartitions (OutboxTableName, PartitionId) VALUES (N'Outbox', @i);
    SET @i = @i + 1;
END;

-- 6. Start publishers
```

#### PostgreSQL Procedure

```sql
-- 1. Stop all publishers first!

-- 2. Remove existing partitions
DELETE FROM outbox_partitions WHERE outbox_table_name = 'outbox';

-- 3. Seed new partition count
INSERT INTO outbox_partitions (outbox_table_name, partition_id)
SELECT 'outbox', gs FROM generate_series(0, <NEW_COUNT> - 1) gs
ON CONFLICT DO NOTHING;

-- 4. Clean up stale publishers (optional)
DELETE FROM outbox_publishers;

-- 5. Start publishers
```

No schema changes needed for PostgreSQL — the hash modulus is derived at query time from the partition row count.

#### Ordering safety

When publishers are stopped, the change is safe: no in-flight messages, each key maps to exactly one partition under the new modulus. If the outbox was not fully drained, some messages may be redelivered after restart (at-least-once semantics — consumers must be idempotent).
```

- [ ] **Step 3: Update sqlserver-eventhub-publisher-reference.md**

In `docs/sqlserver-eventhub-publisher-reference.md`, replace the FetchBatch query in section 2.2 (around line 108-123) with the new query:

```sql
SELECT TOP (@BatchSize)
    o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType,
    o.Headers, o.Payload, o.PayloadContentType,
    o.EventDateTimeUtc, o.EventOrdinal,
    o.RetryCount, o.CreatedAtUtc
FROM dbo.Outbox o
WHERE o.PartitionId IN (
    SELECT op.PartitionId
    FROM dbo.OutboxPartitions op
    WHERE op.OutboxTableName = @OutboxTableName
      AND op.OwnerPublisherId = @PublisherId
      AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
)
  AND o.RetryCount < @MaxRetryCount
  AND o.RowVersion < MIN_ACTIVE_ROWVERSION()
ORDER BY o.EventDateTimeUtc, o.EventOrdinal;
```

Update the parameters list — remove `@TotalPartitions`.

Update the filter table — replace the `ABS(CHECKSUM...)` row with:

```markdown
| `o.PartitionId IN (subquery)`                       | Only fetch from partitions this publisher owns (precomputed hash)   |
```

- [ ] **Step 4: Update architecture.md**

In `docs/architecture.md`, update the partition ownership section (around line 183-189). Replace:

```markdown
PostgreSQL uses `hashtext()` and SQL Server uses `CHECKSUM()`—different functions, producing different mappings. By default, 64 partitions are seeded per outbox table.
```

With:

```markdown
PostgreSQL uses `hashtext()` (computed at query time) and SQL Server uses `CHECKSUM()` (precomputed as a persisted column `PartitionId`). Different functions, producing different mappings. PostgreSQL seeds 64 partitions by default; SQL Server seeds 128 (the modulus is baked into the computed column formula).
```

- [ ] **Step 5: Update publisher-flow.md partition hashing section**

In `docs/publisher-flow.md`, in the `HASH` subgraph of the mermaid diagram (around line 85-90), update the SQL Server line from:

```
HA3 --> HA3["PG: hashtext(key) & 0x7FFFFFFF\nSQL: ABS(CHECKSUM(key))"]
```

To:

```
HA3 --> HA3["PG: hashtext(key) & 0x7FFFFFFF (query time)\nSQL: persisted PartitionId column"]
```

In the "Partition hashing and ownership" text section (around line 133-134), update:

```markdown
- **SQL Server:** `ABS(CAST(CHECKSUM(key) AS BIGINT)) % total_partitions`
```

To:

```markdown
- **SQL Server:** `PartitionId` persisted computed column — `ABS(CAST(CHECKSUM(key) AS BIGINT)) % 128` (precomputed at INSERT time, used as index leading key)
```

- [ ] **Step 6: Verify all docs are consistent**

Grep for stale references:

```bash
grep -rn "CHECKSUM.*TotalPartitions\|% @TotalPartitions\|% total_partitions.*SQL Server" docs/
```

Expected: No matches in the main docs (the old design spec in `docs/superpowers/specs/` is historical and doesn't need updating).

- [ ] **Step 7: Commit**

```bash
git add docs/
git commit -m "docs: update all documentation for precomputed PartitionId and partition count safety"
```

---

### Task 5: Run Full Test Suite and Performance Verification

**Files:**
- No new files — verification only

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test tests/Outbox.Core.Tests/ && dotnet test tests/Outbox.Kafka.Tests/ && dotnet test tests/Outbox.EventHub.Tests/ && dotnet test tests/Outbox.Store.Tests/`
Expected: All pass.

- [ ] **Step 2: Run SQL Server integration tests**

Run: `dotnet test tests/Outbox.IntegrationTests/ --filter "FullyQualifiedName~SqlServer"`
Expected: All SQL Server integration tests pass with the new schema.

- [ ] **Step 3: Run SQL Server performance tests**

Run: `dotnet test tests/Outbox.PerformanceTests/ --filter "FullyQualifiedName~BulkDrain" --logger "console;verbosity=detailed"`
Expected: All 12 bulk tests pass. SQL Server poll p50 should drop from ~130ms to ~10-20ms.

- [ ] **Step 4: Commit any fixes**

If any tests fail, fix and commit:
```bash
git add -A
git commit -m "fix: resolve issues from precomputed PartitionId verification"
```

---

## Notes for Execution

- **The `IOutboxStore` interface is unchanged** — `FetchBatchAsync` has the same signature. Only the SQL Server implementation changes internally.
- **PostgreSQL is not touched** — its queries, schema, and partition count remain as-is.
- **Integration tests use `install.sql`** to create the schema, so they automatically pick up the computed column and new index.
- **The `GetCachedPartitionCountAsync` method stays** in `SqlServerOutboxStore` — it's still called by the publish loop for worker index computation, and by rebalance/orphan sweep queries for fair-share calculation.
- **Performance test fixture also uses `install.sql`** — no changes needed to the perf test infrastructure.
