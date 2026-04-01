# Remove Leasing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove per-message leasing from the outbox publisher, replacing `SELECT+UPDATE(lease) → send → DELETE/UPDATE(release)` with `SELECT → send → DELETE`, eliminating RowVer/xmin changes that break the version ceiling ordering filter.

**Architecture:** Partition ownership (single-writer guarantee) replaces per-message leasing. The publish loop is sequential (one poll completes before the next), so no concurrent access to the same messages. `IncrementRetryCountAsync` replaces `ReleaseLeaseAsync` for transport failures. Dead-letter sweep simplifies to `WHERE retry_count >= MaxRetryCount`.

**Tech Stack:** .NET, Dapper, PostgreSQL, SQL Server, xUnit, NSubstitute

**Spec:** `docs/superpowers/specs/2026-03-31-remove-leasing-design.md`

---

## File Map

| File | Action | Purpose |
|------|--------|---------|
| `src/Outbox.Core/Abstractions/IOutboxStore.cs` | Modify | Remove `ReleaseLeaseAsync`, rename `LeaseBatchAsync` → `FetchBatchAsync`, simplify signatures |
| `src/Outbox.Core/Options/OutboxPublisherOptions.cs` | Modify | Remove `LeaseDurationSeconds` and its validation |
| `src/Outbox.Core/Engine/OutboxPublisherService.cs` | Modify | Remove lease/release logic, add `IncrementRetryCountAsync` calls |
| `src/Outbox.PostgreSQL/PostgreSqlQueries.cs` | Modify | Replace LeaseBatch CTE+UPDATE with plain SELECT, remove release queries, add IncrementRetryCount |
| `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs` | Modify | Rename `LeaseBatchAsync` → `FetchBatchAsync`, remove `ReleaseLeaseAsync`, add `IncrementRetryCountAsync`, simplify `DeletePublishedAsync`/`DeadLetterAsync` |
| `src/Outbox.PostgreSQL/db_scripts/install.sql` | Modify | Remove `leased_until_utc`, `lease_owner` columns and old indexes |
| `src/Outbox.SqlServer/SqlServerQueries.cs` | Modify | Same as PostgreSQL queries |
| `src/Outbox.SqlServer/SqlServerOutboxStore.cs` | Modify | Same as PostgreSQL store |
| `src/Outbox.SqlServer/db_scripts/install.sql` | Modify | Remove lease columns, update indexes |
| `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs` | Modify | Update mocks: `FetchBatchAsync`, `IncrementRetryCountAsync`, remove `ReleaseLeaseAsync` |
| `tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs` | Modify | Remove `LeaseDurationSeconds` validation tests |
| `tests/Outbox.Core.Tests/OutboxMessageInterceptorOrchestrationTests.cs` | Modify | Update store mock calls |
| `tests/Outbox.Core.Tests/OutboxHealthCheckTests.cs` | Modify | Update store mock calls |
| `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs` | Modify | Use `FetchBatchAsync` instead of `LeaseBatchAsync` |
| `tests/Outbox.IntegrationTests/Scenarios/SqlServer/SqlServerConcurrentTransactionOrderingTests.cs` | Modify | Same |
| `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs` | Modify | Remove lease columns from insert/cleanup SQL |
| `tests/Outbox.IntegrationTests/Helpers/SqlServerTestHelper.cs` | Modify | Same |
| `docs/outbox-requirements-invariants.md` | Modify | Update invariants to reflect no-lease architecture |

---

### Task 1: Update IOutboxStore interface and OutboxPublisherOptions

**Files:**
- Modify: `src/Outbox.Core/Abstractions/IOutboxStore.cs`
- Modify: `src/Outbox.Core/Options/OutboxPublisherOptions.cs`

This is the foundation — all other tasks depend on it.

- [ ] **Step 1: Update IOutboxStore interface**

Replace the full interface in `src/Outbox.Core/Abstractions/IOutboxStore.cs`:

```csharp
// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

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

Changes:
- `LeaseBatchAsync` → `FetchBatchAsync` (removed `leaseDurationSeconds` parameter)
- `DeletePublishedAsync` — removed `publisherId` parameter
- `DeadLetterAsync` — removed `publisherId` parameter
- `ReleaseLeaseAsync` — removed entirely
- `IncrementRetryCountAsync` — new method

- [ ] **Step 2: Remove LeaseDurationSeconds from OutboxPublisherOptions**

In `src/Outbox.Core/Options/OutboxPublisherOptions.cs`, remove the `LeaseDurationSeconds` property and its validation constraint (`LeaseDurationSeconds < PartitionGracePeriodSeconds`). Search for all references to `LeaseDurationSeconds` in the `Validate` method and remove the relevant `ValidationResult`.

- [ ] **Step 3: Verify it compiles (expect failures in stores and service)**

```bash
dotnet build src/Outbox.Core/ --no-restore
```

Expected: Build succeeds (interface and options are just definitions).

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.Core/Abstractions/IOutboxStore.cs src/Outbox.Core/Options/OutboxPublisherOptions.cs
git commit -m "refactor: update IOutboxStore interface — remove leasing, add FetchBatch and IncrementRetryCount"
```

---

### Task 2: Update PostgreSQL schema and queries

**Files:**
- Modify: `src/Outbox.PostgreSQL/db_scripts/install.sql`
- Modify: `src/Outbox.PostgreSQL/PostgreSqlQueries.cs`

- [ ] **Step 1: Update PostgreSQL install.sql**

Remove `leased_until_utc` and `lease_owner` columns from the outbox table. Remove old indexes (`ix_outbox_unleased`, `ix_outbox_lease_expiry`, `ix_outbox_sweep`). Add new `ix_outbox_pending` index. Remove lease columns from the diagnostic view `vw_outbox`.

The outbox table becomes:

```sql
CREATE TABLE IF NOT EXISTS outbox
(
    sequence_number    BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    topic_name         VARCHAR(256)   NOT NULL,
    partition_key      VARCHAR(256)   NOT NULL,
    event_type         VARCHAR(256)   NOT NULL,
    headers            VARCHAR(2000)  NULL,
    payload            BYTEA          NOT NULL,
    created_at_utc     TIMESTAMPTZ(3) NOT NULL DEFAULT clock_timestamp(),
    event_datetime_utc TIMESTAMPTZ(3) NOT NULL,
    event_ordinal      INT            NOT NULL DEFAULT 0,
    payload_content_type VARCHAR(100) NOT NULL DEFAULT 'application/json',
    retry_count        INT            NOT NULL DEFAULT 0,

    CONSTRAINT pk_outbox PRIMARY KEY (sequence_number)
);
```

New index:

```sql
CREATE INDEX IF NOT EXISTS ix_outbox_pending
ON outbox (event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, retry_count, created_at_utc);
```

- [ ] **Step 2: Update PostgreSqlQueries.cs**

Replace the `LeaseBatch` query with `FetchBatch` (plain SELECT, no CTE, no UPDATE):

```csharp
FetchBatch = $@"
SELECT o.sequence_number, o.topic_name, o.partition_key, o.event_type,
       o.headers, o.payload, o.payload_content_type,
       o.event_datetime_utc, o.event_ordinal,
       o.retry_count, o.created_at_utc
FROM {outboxTable} o
INNER JOIN {partitionsTable} op
    ON  op.outbox_table_name = @outbox_table_name
    AND op.owner_publisher_id = @publisher_id
    AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < clock_timestamp())
    AND ((hashtext(o.partition_key) & 2147483647) % @total_partitions) = op.partition_id
WHERE o.retry_count < @max_retry_count
  AND o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
ORDER BY o.event_datetime_utc, o.event_ordinal
LIMIT @batch_size;";
```

Replace `DeletePublished` (remove `lease_owner` check):

```csharp
DeletePublished = $@"
DELETE FROM {outboxTable}
WHERE  sequence_number = ANY(@ids);";
```

Add `IncrementRetryCount`:

```csharp
IncrementRetryCount = $@"
UPDATE {outboxTable}
SET    retry_count = retry_count + 1
WHERE  sequence_number = ANY(@ids);";
```

Remove `ReleaseLeaseWithRetry` and `ReleaseLeaseNoRetry` query properties.

Update `DeadLetter` — remove `lease_owner` check from the WHERE clause.

Update `SweepDeadLetters` — simplify to just `WHERE retry_count >= @max_retry_count` with `FOR UPDATE SKIP LOCKED`.

Update `GetPendingCount` — simplify to `SELECT COUNT(*) FROM {outboxTable}` (no lease filter).

Update property declarations at the top of the class to match: rename `LeaseBatch` → `FetchBatch`, remove `ReleaseLeaseWithRetry`/`ReleaseLeaseNoRetry`, add `IncrementRetryCount`.

- [ ] **Step 3: Build**

```bash
dotnet build src/Outbox.PostgreSQL/ --no-restore
```

Expected: Build fails (store implementation still references old interface). That's expected — Task 3 will fix it.

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.PostgreSQL/db_scripts/install.sql src/Outbox.PostgreSQL/PostgreSqlQueries.cs
git commit -m "refactor: PostgreSQL schema and queries — remove leasing, add version ceiling"
```

---

### Task 3: Update PostgreSQL store implementation

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs`

- [ ] **Step 1: Update PostgreSqlOutboxStore**

Rename `LeaseBatchAsync` → `FetchBatchAsync`. Remove `leaseDurationSeconds` parameter. Remove `lease_duration_seconds` from the query parameters. Use `_queries.FetchBatch` instead of `_queries.LeaseBatch`.

Remove `ReleaseLeaseAsync` method entirely.

Add `IncrementRetryCountAsync`:

```csharp
public async Task IncrementRetryCountAsync(
    IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
{
    var parameters = new DynamicParameters();
    parameters.Add("@ids", new BigintArrayParam(sequenceNumbers));
    await _db.ExecuteAsync(_queries.IncrementRetryCount, parameters, ct).ConfigureAwait(false);
}
```

Update `DeletePublishedAsync` — remove `publisherId` parameter and `publisher_id` from query parameters.

Update `DeadLetterAsync` — remove `publisherId` parameter and `publisher_id` from query parameters.

- [ ] **Step 2: Build**

```bash
dotnet build src/Outbox.PostgreSQL/ --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs
git commit -m "refactor: PostgreSQL store — implement FetchBatch, IncrementRetryCount, remove ReleaseLeaseAsync"
```

---

### Task 4: Update SQL Server schema, queries, and store

**Files:**
- Modify: `src/Outbox.SqlServer/db_scripts/install.sql`
- Modify: `src/Outbox.SqlServer/SqlServerQueries.cs`
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs`

- [ ] **Step 1: Update SQL Server install.sql**

Same structural changes as PostgreSQL: remove `LeasedUntilUtc` and `LeaseOwner` columns, remove old indexes, add `IX_Outbox_Pending`. Keep `RowVer ROWVERSION`. Update diagnostic view.

The Outbox table becomes:

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

- [ ] **Step 2: Update SqlServerQueries.cs**

Same pattern as PostgreSQL queries: rename `LeaseBatch` → `FetchBatch` (plain SELECT with `READPAST`, version ceiling), simplify `DeletePublished`, add `IncrementRetryCount`, remove `ReleaseLeaseWithRetry`/`ReleaseLeaseNoRetry`, simplify `DeadLetter`, simplify `SweepDeadLetters`, simplify `GetPendingCount`.

FetchBatch query:

```csharp
FetchBatch = $"""
              SELECT TOP (@BatchSize)
                  o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType,
                  o.Headers, o.Payload, o.PayloadContentType,
                  o.EventDateTimeUtc, o.EventOrdinal,
                  o.RetryCount, o.CreatedAtUtc
              FROM {outboxTable} o WITH (ROWLOCK, READPAST)
              INNER JOIN {partitionsTable} op
                  ON  op.OutboxTableName = @OutboxTableName
                  AND op.OwnerPublisherId = @PublisherId
                  AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
                  AND (ABS(CAST(CHECKSUM(o.PartitionKey) AS BIGINT)) % @TotalPartitions) = op.PartitionId
              WHERE o.RetryCount < @MaxRetryCount
                AND o.RowVer < MIN_ACTIVE_ROWVERSION()
              ORDER BY o.EventDateTimeUtc, o.EventOrdinal;
              """;
```

IncrementRetryCount:

```csharp
IncrementRetryCount = $"""
                       UPDATE o SET o.RetryCount = o.RetryCount + 1
                       FROM {outboxTable} o
                       INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber;
                       """;
```

- [ ] **Step 3: Update SqlServerOutboxStore.cs**

Same changes as PostgreSQL store: rename `LeaseBatchAsync` → `FetchBatchAsync`, remove `ReleaseLeaseAsync`, add `IncrementRetryCountAsync`, update `DeletePublishedAsync` and `DeadLetterAsync` signatures.

- [ ] **Step 4: Build both store projects**

```bash
dotnet build src/Outbox.SqlServer/ --no-restore
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.SqlServer/
git commit -m "refactor: SQL Server schema, queries, and store — remove leasing, add version ceiling"
```

---

### Task 5: Update OutboxPublisherService

**Files:**
- Modify: `src/Outbox.Core/Engine/OutboxPublisherService.cs`

This is the largest change. The publish loop needs to be updated to use the new store interface.

- [ ] **Step 1: Update PublishLoopAsync (lines 235-420)**

Key changes:

1. Replace `_store.LeaseBatchAsync(publisherId, opts.BatchSize, opts.LeaseDurationSeconds, opts.MaxRetryCount, ct)` with `_store.FetchBatchAsync(publisherId, opts.BatchSize, opts.MaxRetryCount, ct)`.

2. Remove the `unprocessedSequences` ConcurrentDictionary (lines 284-285) and ALL references to it throughout the method.

3. Remove the `finally` block (lines 369-389) that releases unprocessed leases.

4. Update `DeadLetterAsync` call for poison messages — remove `publisherId` parameter (line 290-293):
   ```csharp
   await _store.DeadLetterAsync(
       poison.Select(m => m.SequenceNumber).ToList(),
       "Max retry count exceeded",
       CancellationToken.None);
   ```

5. Update `ProcessGroupsAsync` call — remove `unprocessedSequences` parameter.

- [ ] **Step 2: Update ProcessGroupsAsync (lines 425-693)**

Remove `unprocessedSequences` parameter from the method signature.

**Circuit breaker open (lines 441-449):** Replace lease release with no-op:
```csharp
if (circuitBreaker.IsOpen(topicName))
{
    continue;
}
```

**Success path — DeletePublishedAsync (line 511):** Remove `publisherId`:
```csharp
await _store.DeletePublishedAsync(sequenceNumbers, ct);
```

**Success path — delete failure (lines 513-531):** Remove the `ReleaseLeaseAsync` call. Just log the warning:
```csharp
catch (Exception deleteEx)
{
    _logger.LogWarning(deleteEx,
        "Failed to delete {Count} published messages — they will be re-delivered on next poll",
        sequenceNumbers.Count);
}
```

**Remove all `unprocessedSequences.TryRemove` calls** throughout the method.

**Transport failure (lines 632-688):** Replace `ReleaseLeaseAsync(incrementRetry: true)` with `IncrementRetryCountAsync`:
```csharp
try
{
    await _store.IncrementRetryCountAsync(sequenceNumbers, CancellationToken.None);
}
catch (Exception incrementEx)
{
    _logger.LogWarning(incrementEx,
        "Failed to increment retry count for {Count} messages — they will be retried without increment on next poll",
        sequenceNumbers.Count);
}
```

**Partial send — PartialSendException (lines 538-630):**
- Succeeded messages: `DeletePublishedAsync` without publisherId. On delete failure, just log (remove `ReleaseLeaseAsync` fallback).
- Failed messages: `IncrementRetryCountAsync` instead of `ReleaseLeaseAsync(incrementRetry: true)`.
- Remove all `unprocessedSequences` references.

**Cancellation break (line 533-536):** Remove comment about lease release:
```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    break;
}
```

- [ ] **Step 3: Build**

```bash
dotnet build src/Outbox.Core/ --no-restore
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.Core/Engine/OutboxPublisherService.cs
git commit -m "refactor: OutboxPublisherService — remove lease/release, use FetchBatch + IncrementRetryCount"
```

---

### Task 6: Update unit tests

**Files:**
- Modify: `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs`
- Modify: `tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs`
- Modify: `tests/Outbox.Core.Tests/OutboxMessageInterceptorOrchestrationTests.cs`
- Modify: `tests/Outbox.Core.Tests/OutboxHealthCheckTests.cs`

This task has ~111 references to update across 4 files.

- [ ] **Step 1: Update OutboxPublisherServiceTests.cs**

This is the largest test file. Key changes:

1. All `_store.LeaseBatchAsync(...)` mock setups → `_store.FetchBatchAsync(...)`. Remove `leaseDurationSeconds` argument from the `Arg.Any<>()` matchers.

2. All `_store.ReleaseLeaseAsync(...)` mock setups and verifications → remove entirely or replace with `_store.IncrementRetryCountAsync(...)` where appropriate.

3. All `_store.DeletePublishedAsync(Arg.Any<string>(), ...)` → remove the `publisherId` argument.

4. All `_store.DeadLetterAsync(Arg.Any<string>(), ...)` → remove the `publisherId` argument.

5. Remove any test that specifically verifies lease release behavior (e.g., "should release lease on circuit open", "should release unprocessed on shutdown"). Replace with:
   - Circuit open: verify no store calls are made (do nothing).
   - Shutdown: verify `UnregisterPublisherAsync` is called but no release.

6. Add/update test: "should call IncrementRetryCountAsync on transport failure" — verify `IncrementRetryCountAsync` is called with the failed sequence numbers.

- [ ] **Step 2: Update OutboxPublisherOptionsValidatorTests.cs**

Remove the test for `LeaseDurationSeconds < PartitionGracePeriodSeconds` validation. Remove any test that sets or validates `LeaseDurationSeconds`.

- [ ] **Step 3: Update OutboxMessageInterceptorOrchestrationTests.cs**

Update mock setups for `FetchBatchAsync` (was `LeaseBatchAsync`), `DeletePublishedAsync` (remove publisherId), and remove any `ReleaseLeaseAsync` references.

- [ ] **Step 4: Update OutboxHealthCheckTests.cs**

Update store mock setups if any reference `LeaseBatchAsync` or lease-related methods.

- [ ] **Step 5: Build and run unit tests**

```bash
dotnet build tests/Outbox.Core.Tests/ --no-restore && dotnet test tests/Outbox.Core.Tests/ -v n
```

Expected: All 136 tests pass (some may be removed/modified, count may change).

- [ ] **Step 6: Commit**

```bash
git add tests/Outbox.Core.Tests/
git commit -m "test: update unit tests for no-lease architecture"
```

---

### Task 7: Update integration tests and helpers

**Files:**
- Modify: `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs`
- Modify: `tests/Outbox.IntegrationTests/Helpers/SqlServerTestHelper.cs`
- Modify: `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs`
- Modify: `tests/Outbox.IntegrationTests/Scenarios/SqlServer/SqlServerConcurrentTransactionOrderingTests.cs`

- [ ] **Step 1: Update OutboxTestHelper.cs**

In `InsertMessagesAsync`: remove `leased_until_utc` and `lease_owner` from any INSERT SQL (they're no longer in the table).

In `CleanupAsync`: the TRUNCATE statement doesn't reference column names, so it's unchanged.

In `GetRetryCountsAsync`: unchanged (doesn't reference lease columns).

- [ ] **Step 2: Update SqlServerTestHelper.cs**

Same changes as OutboxTestHelper — remove any lease column references from INSERT SQL.

- [ ] **Step 3: Update ConcurrentTransactionOrderingTests.cs**

Replace `store.LeaseBatchAsync(publisherId, batchSize: 10, leaseDurationSeconds: 30, maxRetryCount: 5, ...)` with `store.FetchBatchAsync(publisherId, batchSize: 10, maxRetryCount: 5, ...)`.

Replace `store.DeletePublishedAsync(publisherId, ...)` with `store.DeletePublishedAsync(...)`.

Replace `store.UnregisterPublisherAsync(publisherId, ...)` — unchanged (still has publisherId).

- [ ] **Step 4: Update SqlServerConcurrentTransactionOrderingTests.cs**

Same changes as the PostgreSQL test.

- [ ] **Step 5: Build and run integration tests**

```bash
dotnet build tests/Outbox.IntegrationTests/ --no-restore
dotnet test tests/Outbox.IntegrationTests/ --filter "FullyQualifiedName~ConcurrentTransactionOrderingTests" -v n
```

Expected: All 4 concurrent ordering tests pass.

- [ ] **Step 6: Commit**

```bash
git add tests/Outbox.IntegrationTests/
git commit -m "test: update integration tests and helpers for no-lease architecture"
```

---

### Task 8: Run full test suite

- [ ] **Step 1: Run all unit tests**

```bash
dotnet test tests/Outbox.Core.Tests/ -v n
```

Expected: All pass.

- [ ] **Step 2: Run other unit test projects**

```bash
dotnet test tests/Outbox.Kafka.Tests/ -v n
dotnet test tests/Outbox.EventHub.Tests/ -v n
dotnet test tests/Outbox.Store.Tests/ -v n
```

Expected: All pass. These projects may have compilation errors if they reference `IOutboxStore` methods — fix any that arise.

- [ ] **Step 3: Run integration tests**

```bash
dotnet test tests/Outbox.IntegrationTests/ --filter "FullyQualifiedName~ConcurrentTransactionOrderingTests" -v n
```

Expected: 4/4 pass.

---

### Task 9: Update documentation

**Files:**
- Modify: `docs/outbox-requirements-invariants.md`

- [ ] **Step 1: Update invariants document**

Key changes:

1. **Message ordering section:** Update to reflect no-lease architecture. The version ceiling filter (`xmin`/`RowVer`) is now the primary ordering mechanism. Remove references to "lease before later ones."

2. **Lease ownership section:** Remove entirely or replace with "Partition ownership" section explaining that partition assignment is the sole isolation mechanism.

3. **Graceful shutdown section:** Update — publisher calls `UnregisterPublisherAsync` only (no lease release). Messages stay in the table for the next publisher.

4. **Unprocessed sequences safety net (BUG-5) section:** Remove — no longer applicable without leasing.

5. **Store Contract section:** Update `LeaseBatch` → `FetchBatch`, remove `ReleaseLease`, add `IncrementRetryCount`, update `DeletePublished` and `DeadLetter` (no lease_owner check), update `SweepDeadLetters` (simplified condition).

6. **Configuration Constraints:** Remove `LeaseDurationSeconds < PartitionGracePeriodSeconds`.

7. **Anti-Patterns:** Remove "Using ct (linked token) for cleanup operations" references to `ReleaseLeaseAsync`.

- [ ] **Step 2: Commit**

```bash
git add docs/outbox-requirements-invariants.md
git commit -m "docs: update invariants for no-lease architecture with version ceiling ordering"
```
