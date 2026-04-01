# Version Ceiling Ordering Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent LeaseBatchAsync from returning committed rows when earlier (lower `event_datetime_utc`) uncommitted rows exist for the same partition key, by adding a version ceiling filter to the LeaseBatch query.

**Architecture:** Add a `WHERE` clause to LeaseBatch that filters out rows whose transaction version is at or above the minimum active transaction version. SQL Server uses a new `RowVer ROWVERSION` column with `MIN_ACTIVE_ROWVERSION()`. PostgreSQL uses the built-in `xmin` system column with `pg_snapshot_xmin(pg_current_snapshot())`. Both approaches ensure the processor only sees rows from fully settled transactions.

**Tech Stack:** PostgreSQL (xmin, pg_current_snapshot), SQL Server (ROWVERSION, MIN_ACTIVE_ROWVERSION), Dapper, xUnit integration tests with Testcontainers.

---

## File Map

| File | Action | Purpose |
|------|--------|---------|
| `src/Outbox.PostgreSQL/PostgreSqlQueries.cs` | Modify | Add xmin ceiling filter to LeaseBatch query |
| `src/Outbox.SqlServer/SqlServerQueries.cs` | Modify | Add RowVer ceiling filter to LeaseBatch query |
| `src/Outbox.SqlServer/db_scripts/install.sql` | Modify | Add RowVer ROWVERSION column to Outbox table |
| `src/Outbox.SqlServer/db_scripts/migrate-001-add-rowversion.sql` | Create | Migration script for existing deployments |
| `src/Outbox.PostgreSQL/db_scripts/install.sql` | No change | xmin is a built-in system column, no schema change |
| `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs` | Already exists | PostgreSQL failing test |
| `tests/Outbox.IntegrationTests/Scenarios/SqlServer/SqlServerConcurrentTransactionOrderingTests.cs` | Already exists | SQL Server failing test |
| `docs/outbox-requirements-invariants.md` | Modify | Update ordering invariant to document version ceiling |

---

### Task 1: Verify the tests fail with the current implementation

**Files:**
- Test: `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs`
- Test: `tests/Outbox.IntegrationTests/Scenarios/SqlServer/SqlServerConcurrentTransactionOrderingTests.cs`

- [ ] **Step 1: Run the PostgreSQL concurrent ordering test**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~ConcurrentTransactionOrderingTests.LeaseBatch_WithUncommittedEarlierRows_ShouldNotReturnLaterCommittedRows" \
  --no-build -v n
```

Expected: FAIL — `Assert.Empty()` fails because LeaseBatchAsync returns B, C (the committed rows) while Txn #1 is still open.

- [ ] **Step 2: Run the SQL Server concurrent ordering test**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~SqlServerConcurrentTransactionOrderingTests.LeaseBatch_WithUncommittedEarlierRows_ShouldNotReturnLaterCommittedRows" \
  --no-build -v n
```

Expected: FAIL — same reason as PostgreSQL.

---

### Task 2: Add RowVer column to SQL Server schema

**Files:**
- Modify: `src/Outbox.SqlServer/db_scripts/install.sql`
- Create: `src/Outbox.SqlServer/db_scripts/migrate-001-add-rowversion.sql`

- [ ] **Step 1: Add RowVer column to install.sql**

In `src/Outbox.SqlServer/db_scripts/install.sql`, add the `RowVer` column to the `Outbox` table definition (after `RetryCount`):

```sql
        RowVer           ROWVERSION        NOT NULL,
```

The full column block becomes:

```sql
        RetryCount       INT                   NOT NULL  DEFAULT 0,
        RowVer           ROWVERSION            NOT NULL,

        CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (SequenceNumber)
```

`ROWVERSION` is automatically assigned by SQL Server on INSERT and updated on UPDATE. No default needed.

- [ ] **Step 2: Create migration script for existing deployments**

Create `src/Outbox.SqlServer/db_scripts/migrate-001-add-rowversion.sql`:

```sql
-- Migration: Add RowVer column for version ceiling ordering guarantee.
-- ROWVERSION is auto-populated for existing rows on ALTER TABLE ADD.
-- Safe to run multiple times (idempotent via IF NOT EXISTS check).

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Outbox')
      AND name = N'RowVer'
)
BEGIN
    ALTER TABLE dbo.Outbox ADD RowVer ROWVERSION NOT NULL;
END;
GO
```

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.SqlServer/db_scripts/install.sql \
        src/Outbox.SqlServer/db_scripts/migrate-001-add-rowversion.sql
git commit -m "schema: add RowVer ROWVERSION column to SQL Server Outbox table"
```

---

### Task 3: Apply migration to test container

The SQL Server Testcontainer runs the `install.sql` script at startup. Since we modified `install.sql` in Task 2, new test runs will have the column. However, the existing running containers from other test runs won't. The `InfrastructureFixture` already runs the schema at `InitializeAsync`, so this is handled automatically.

- [ ] **Step 1: Verify the migration is applied by the fixture**

No code change needed. The `InfrastructureFixture.RunSqlServerSchemaAsync()` reads and executes `install.sql`, which now includes the `RowVer` column. Testcontainers are created fresh for each test run.

- [ ] **Step 2: Build to verify install.sql is valid**

```bash
dotnet build tests/Outbox.IntegrationTests/ --no-restore
```

Expected: Build succeeded.

---

### Task 4: Add version ceiling filter to PostgreSQL LeaseBatch query

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlQueries.cs:62-88`

- [ ] **Step 1: Add the xmin ceiling filter to the LeaseBatch query**

In `src/Outbox.PostgreSQL/PostgreSqlQueries.cs`, modify the `LeaseBatch` query. Add this line to the `WHERE` clause, after the `retry_count` check:

```
      AND o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
```

The full `LeaseBatch` query becomes:

```csharp
        LeaseBatch = $@"
WITH batch AS (
    SELECT o.sequence_number
    FROM {outboxTable} o
    INNER JOIN {partitionsTable} op
        ON  op.outbox_table_name = @outbox_table_name
        AND op.owner_publisher_id = @publisher_id
        AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < clock_timestamp())
        AND ((hashtext(o.partition_key) & 2147483647) % @total_partitions) = op.partition_id
    WHERE (o.leased_until_utc IS NULL OR o.leased_until_utc < clock_timestamp())
      AND o.retry_count < @max_retry_count
      AND o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
    ORDER BY o.event_datetime_utc, o.event_ordinal
    LIMIT @batch_size
    FOR UPDATE OF o SKIP LOCKED
)
UPDATE {outboxTable} o
SET    leased_until_utc = clock_timestamp() + make_interval(secs => @lease_duration_seconds),
       lease_owner      = @publisher_id,
       retry_count      = CASE WHEN o.leased_until_utc IS NOT NULL
                               THEN o.retry_count + 1
                               ELSE o.retry_count END
FROM   batch b
WHERE  o.sequence_number = b.sequence_number
RETURNING o.sequence_number, o.topic_name, o.partition_key, o.event_type,
          o.headers, o.payload, o.payload_content_type,
          o.event_datetime_utc, o.event_ordinal,
          o.retry_count, o.created_at_utc;";
```

**How this works:**
- `xmin` is a system column on every PostgreSQL row — the transaction ID that inserted it.
- `pg_current_snapshot()` returns the set of in-progress transaction IDs.
- `pg_snapshot_xmin(snapshot)` returns the lowest active transaction ID.
- `xmin < snapshot_xmin` means the row's inserting transaction is fully committed and older than any currently active transaction.
- Rows from committed transactions that started AFTER a still-active transaction are excluded — this is what prevents the ordering gap.

- [ ] **Step 2: Build to verify the change compiles**

```bash
dotnet build src/Outbox.PostgreSQL/ --no-restore
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.PostgreSQL/PostgreSqlQueries.cs
git commit -m "fix: add xmin version ceiling to PostgreSQL LeaseBatch to prevent cross-transaction ordering violations"
```

---

### Task 5: Run PostgreSQL test to verify it passes

**Files:**
- Test: `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs`

- [ ] **Step 1: Run the primary ordering test**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~ConcurrentTransactionOrderingTests.LeaseBatch_WithUncommittedEarlierRows_ShouldNotReturnLaterCommittedRows" \
  -v n
```

Expected: PASS — LeaseBatchAsync returns empty while Txn #1 is open, then returns all 3 messages in correct order after both transactions commit.

- [ ] **Step 2: Run the cross-partition test**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~ConcurrentTransactionOrderingTests.LeaseBatch_WithUncommittedRowOnDifferentPartitionKey_ShouldStillReturnOtherPartitions" \
  -v n
```

Expected: PASS — all 4 messages eventually delivered (the xmin ceiling may block fast-key temporarily while slow-key's transaction is open, but all drain after commit).

- [ ] **Step 3: Run all existing PostgreSQL tests to ensure no regressions**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~Outbox.IntegrationTests.Scenarios.OrderingTests|FullyQualifiedName~Outbox.IntegrationTests.Scenarios.BrokerDownTests|FullyQualifiedName~Outbox.IntegrationTests.Scenarios.IntermittentFailureTests|FullyQualifiedName~Outbox.IntegrationTests.Scenarios.PoisonMessageTests" \
  -v n
```

Expected: All PASS.

---

### Task 6: Add version ceiling filter to SQL Server LeaseBatch query

**Files:**
- Modify: `src/Outbox.SqlServer/SqlServerQueries.cs:70-114`

- [ ] **Step 1: Add the RowVer ceiling filter to the LeaseBatch query**

In `src/Outbox.SqlServer/SqlServerQueries.cs`, modify the `LeaseBatch` query. Add this line to the `WHERE` clause inside the `Batch` CTE, after the `RetryCount` check:

```
                            AND o.RowVer < MIN_ACTIVE_ROWVERSION()
```

The full `LeaseBatch` query becomes:

```csharp
        LeaseBatch = $"""
                      WITH Batch AS
                      (
                          SELECT TOP (@BatchSize)
                              o.SequenceNumber,
                              o.TopicName,
                              o.PartitionKey,
                              o.EventType,
                              o.Headers,
                              o.Payload,
                              o.PayloadContentType,
                              o.EventDateTimeUtc,
                              o.EventOrdinal,
                              o.LeasedUntilUtc,
                              o.LeaseOwner,
                              o.RetryCount,
                              o.CreatedAtUtc
                          FROM {outboxTable} o WITH (ROWLOCK, READPAST)
                          INNER JOIN {partitionsTable} op
                              ON  op.OutboxTableName = @OutboxTableName
                              AND op.OwnerPublisherId = @PublisherId
                              AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
                              AND (ABS(CAST(CHECKSUM(o.PartitionKey) AS BIGINT)) % @TotalPartitions) = op.PartitionId
                          WHERE (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME())
                            AND o.RetryCount < @MaxRetryCount
                            AND o.RowVer < MIN_ACTIVE_ROWVERSION()
                          ORDER BY o.EventDateTimeUtc, o.EventOrdinal
                      )
                      UPDATE Batch
                      SET    LeasedUntilUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()),
                             LeaseOwner     = @PublisherId,
                             RetryCount     = CASE WHEN LeasedUntilUtc IS NOT NULL
                                                   THEN RetryCount + 1
                                                   ELSE RetryCount END
                      OUTPUT inserted.SequenceNumber,
                             inserted.TopicName,
                             inserted.PartitionKey,
                             inserted.EventType,
                             inserted.Headers,
                             inserted.Payload,
                             inserted.PayloadContentType,
                             inserted.EventDateTimeUtc,
                             inserted.EventOrdinal,
                             inserted.RetryCount,
                             inserted.CreatedAtUtc;
                      """;
```

**How this works:**
- `RowVer` is a `ROWVERSION` column — SQL Server auto-assigns a monotonically increasing 8-byte value on every INSERT/UPDATE.
- `MIN_ACTIVE_ROWVERSION()` returns the lowest rowversion among all active (uncommitted) write transactions in the database.
- `RowVer < MIN_ACTIVE_ROWVERSION()` ensures we only read rows whose inserting transaction has fully committed and no earlier write transaction is still in-flight.
- Combined with `READPAST`, this gives both non-blocking reads AND ordering safety.

- [ ] **Step 2: Build to verify the change compiles**

```bash
dotnet build src/Outbox.SqlServer/ --no-restore
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.SqlServer/SqlServerQueries.cs
git commit -m "fix: add RowVer version ceiling to SQL Server LeaseBatch to prevent cross-transaction ordering violations"
```

---

### Task 7: Run SQL Server test to verify it passes

**Files:**
- Test: `tests/Outbox.IntegrationTests/Scenarios/SqlServer/SqlServerConcurrentTransactionOrderingTests.cs`

- [ ] **Step 1: Run the primary ordering test**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~SqlServerConcurrentTransactionOrderingTests.LeaseBatch_WithUncommittedEarlierRows_ShouldNotReturnLaterCommittedRows" \
  -v n
```

Expected: PASS.

- [ ] **Step 2: Run the cross-partition test**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~SqlServerConcurrentTransactionOrderingTests.LeaseBatch_WithUncommittedRowOnDifferentPartitionKey_ShouldStillReturnOtherPartitions" \
  -v n
```

Expected: PASS.

- [ ] **Step 3: Run all existing SQL Server tests to ensure no regressions**

```bash
dotnet test tests/Outbox.IntegrationTests/ \
  --filter "FullyQualifiedName~Outbox.IntegrationTests.Scenarios.SqlServer" \
  -v n
```

Expected: All PASS.

---

### Task 8: Run the full test suite

- [ ] **Step 1: Run all unit tests**

```bash
dotnet test tests/Outbox.Core.Tests/ && \
dotnet test tests/Outbox.Kafka.Tests/ && \
dotnet test tests/Outbox.EventHub.Tests/ && \
dotnet test tests/Outbox.Store.Tests/
```

Expected: All PASS (unit tests don't hit real databases, so the SQL changes don't affect them).

- [ ] **Step 2: Run all integration tests**

```bash
dotnet test tests/Outbox.IntegrationTests/ -v n
```

Expected: All PASS. The xmin/RowVer ceiling is a no-op when there are no concurrent write transactions, so existing tests should not be affected.

- [ ] **Step 3: Commit if any test-related fixes were needed**

Only commit if adjustments were required.

---

### Task 9: Update documentation

**Files:**
- Modify: `docs/outbox-requirements-invariants.md:17-19`

- [ ] **Step 1: Update the ordering invariant**

In `docs/outbox-requirements-invariants.md`, replace line 18:

```markdown
- **Across batches for the same partitionKey:** ordering is guaranteed by the SQL `ORDER BY event_datetime_utc, event_ordinal` in LeaseBatch. Earlier messages are leased before later ones.
```

With:

```markdown
- **Across batches for the same partitionKey:** ordering is guaranteed by the SQL `ORDER BY event_datetime_utc, event_ordinal` in LeaseBatch, combined with a version ceiling filter (`xmin < pg_snapshot_xmin()` on PostgreSQL, `RowVer < MIN_ACTIVE_ROWVERSION()` on SQL Server) that withholds rows from committed transactions when earlier write transactions are still in-flight. This prevents the scenario where Transaction #2 commits before Transaction #1 and its rows are published out of order. Trade-off: any concurrent write transaction in the database (even on unrelated tables) temporarily pauses outbox processing until it commits. In typical OLTP workloads, write transactions last < 100ms, so the delay is bounded to one poll cycle.
```

- [ ] **Step 2: Add the version ceiling to the Store Contract**

In `docs/outbox-requirements-invariants.md`, in the Store Contract section under `LeaseBatch` (item 1), append:

```markdown
 MUST filter rows using a version ceiling (`xmin`/`ROWVERSION`) to exclude rows from transactions that committed while earlier write transactions are still active. This prevents cross-transaction ordering violations for the same partition key.
```

- [ ] **Step 3: Commit**

```bash
git add docs/outbox-requirements-invariants.md
git commit -m "docs: document version ceiling ordering guarantee and trade-offs"
```

---

### Task 10: Final verification and cleanup

- [ ] **Step 1: Run the full test suite one final time**

```bash
dotnet test src/Outbox.slnx -v n
```

Expected: All tests PASS.

- [ ] **Step 2: Verify no uncommitted changes remain**

```bash
git status
git diff
```

Expected: Clean working tree.
