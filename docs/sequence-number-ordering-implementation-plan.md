# Sequence-Number Ordering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Switch the outbox `FetchBatch` ordering from `(event_datetime_utc, event_ordinal, sequence_number)` to just `sequence_number`. Drop the `event_ordinal` column from both stores. Keep `event_datetime_utc` in the schema as a debug/forensics column but remove it from the `ORDER BY` and from the `OutboxMessage` ordering contract.

**Architecture:** The "callers insert in delivery order" stipulation is now load-bearing. `sequence_number` (BIGINT IDENTITY on SQL Server, bigserial on PostgreSQL) is monotonic at insert, immutable after insert (the outbox table is INSERT+DELETE only — verified after the in-memory retry change), and uniquely orders rows within and across transactions. Combined with the existing version-ceiling filter (`RowVersion < MIN_ACTIVE_ROWVERSION()` on SQL Server, `xmin < pg_snapshot_xmin(...)` on PostgreSQL), it provides the same ordering guarantee with a simpler contract. `event_datetime_utc` stays in the schema and on the `OutboxMessage` record for debug/observability/dead-letter forensics, but is no longer consulted by the publisher's sort path.

**Tech Stack:** .NET 10, xUnit, SQL Server (Microsoft.Data.SqlClient + Dapper), PostgreSQL (Npgsql + Dapper), Testcontainers for integration tests.

**Reference:** `docs/ordering-column-design-note.md` — the original analysis that recommended this exact change. The "Cost of the simplification" section there walks through the rationale.

---

## File Structure

**Modified (source):**
- `src/Outbox.Core/Models/OutboxMessage.cs` — drop `EventOrdinal` field, update XML doc to describe the new ordering contract.
- `src/Outbox.Core/Engine/OutboxPublisherService.cs` — remove `.ThenBy(m => m.EventOrdinal)` from the in-memory sort inside `ProcessGroupWithRetriesAsync`.
- `src/Outbox.Core/Models/OutboxMessageContext.cs` — drop the `EventOrdinal` accessor property.
- `src/Outbox.Core/Models/DeadLetteredMessage.cs` — drop `EventOrdinal` field.

**Modified (SQL Server store):**
- `src/Outbox.SqlServer/db_scripts/install.sql` — drop `EventOrdinal` columns from both `dbo.Outbox` and `dbo.OutboxDeadLetter`; rebuild `IX_Outbox_Pending` keyed on `(PartitionId, SequenceNumber)`; remove `EventOrdinal` from views.
- `src/Outbox.SqlServer/db_scripts/maintenance/partition_switch_cleanup.sql` — drop `EventOrdinal` from the staging table and the staging index.
- `src/Outbox.SqlServer/SqlServerQueries.cs` — `FetchBatch` `ORDER BY` becomes `o.SequenceNumber` (within partition); drop `o.EventOrdinal` from projection. Drop `EventOrdinal` from `DeadLetter` OUTPUT/INTO. Drop `EventOrdinal` from `DeadLetterGet` SELECT. Drop `EventOrdinal` from `DeadLetterReplay` OUTPUT/INTO.
- `src/Outbox.SqlServer/SqlServerOutboxStore.cs` — no signature changes; just confirm Dapper materialization no longer references the dropped column.
- `src/Outbox.SqlServer/FetchBatchOutputRow.cs` — drop `EventOrdinal` property and constructor argument.

**Modified (PostgreSQL store):**
- `src/Outbox.PostgreSQL/db_scripts/install.sql` — drop `event_ordinal` columns from both `outbox` and `outbox_dead_letter`; rebuild `ix_outbox_pending` keyed on `sequence_number`; remove `event_ordinal` from views.
- `src/Outbox.PostgreSQL/PostgreSqlQueries.cs` — symmetric edits to the SQL Server query file.
- `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs` — no signature changes.
- `src/Outbox.PostgreSQL/FetchBatchOutputRow.cs` — drop `EventOrdinal` property and constructor argument.

**Modified (tests):**
- `tests/Outbox.Core.Tests/TestOutboxServiceFactory.cs` — drop `eventOrdinal` parameter from `MakeMessage` factory.
- `tests/Outbox.Core.Tests/ModelTests.cs` — drop assertions on `EventOrdinal`.
- `tests/Outbox.Core.Tests/OutboxMessageContextTests.cs` — drop assertions on `EventOrdinal` accessor.
- `tests/Outbox.Core.Tests/Behavioral/OrderingContractTests.cs` — assertions about the sort contract need updating; ordering by `EventDateTimeUtc` is no longer the contract.
- `tests/Outbox.Kafka.Tests/KafkaMessageHelperTests.cs`, `KafkaOutboxTransportSendTests.cs` — drop `EventOrdinal` from `OutboxMessage` constructor calls.
- `tests/Outbox.EventHub.Tests/EventHubMessageHelperTests.cs`, `EventHubOutboxTransportSendTests.cs` — same.
- `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs`, `Helpers/SqlServerTestHelper.cs` — drop `event_ordinal`/`EventOrdinal` from INSERT statements that seed the outbox, drop from any SELECT that reads it back.
- `tests/Outbox.IntegrationTests/Scenarios/PoisonMessageTests.cs` (and `SqlServer/SqlServerPoisonMessageTests.cs`) — drop `EventOrdinal` from setup.
- `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs` (and SqlServer variant) — drop `event_ordinal` from fixture inserts.
- `tests/Outbox.IntegrationTests/Scenarios/RebalanceOrderingTests.cs` — drop `event_ordinal`.
- `tests/Outbox.PerformanceTests/Helpers/MessageProducer.cs` — drop `EventOrdinal` from `OutboxMessage` construction or from raw inserts.

**Modified (docs):**
- `docs/outbox-requirements-invariants.md` — update **Message ordering** section, store contract item #1 (`FetchBatch`), anti-pattern about modifying `FetchBatch ORDER BY`. Keep the "callers insert in delivery order" stipulation explicit.
- `docs/architecture.md` — update any mention of `event_datetime_utc, event_ordinal` ordering.
- `docs/publisher-flow.md` — same.
- `docs/known-limitations.md` — same.
- `docs/failure-scenarios-and-integration-tests.md` — same.
- `docs/production-runbook.md` — if it references the column for ops queries.
- `docs/sqlserver-eventhub-publisher-reference.md` — same.
- `docs/sqlserver-query-plan-report.md` — historical, may reference the old index. Add a note that the index has changed; do not rewrite history.
- `src/Outbox.Core/README.md`, `src/Outbox.SqlServer/README.md`, `src/Outbox.PostgreSQL/README.md` — column lists, API descriptions.
- `README.md` (top-level) — only if it documents `OutboxMessage` shape.
- `CLAUDE.md` — review checklist mentions "modifying FetchBatch ORDER BY"; update wording to reflect the new contract.

**Out of scope:**
- The `RowVersion` (SQL Server) / `xmin` (PostgreSQL) **version-ceiling filter** — keep it exactly as it is. It guards against committed-but-not-yet-visible rows from in-flight write transactions. It is independent of the sort key and remains load-bearing.
- The `WITH (NOLOCK)` hint on SQL Server `FetchBatch` — keep it. The justification (outbox table is append-only) is now strictly stronger after the in-memory retry change.
- Renaming `sequence_number` to anything else.
- Any change to the dead-letter table beyond removing the `event_ordinal` column and the corresponding fields from `DeadLetteredMessage`.

---

## Build and test commands

After each major task boundary, run:

```bash
dotnet build src/Outbox.slnx
dotnet test tests/Outbox.Core.Tests/ \
  && dotnet test tests/Outbox.Kafka.Tests/ \
  && dotnet test tests/Outbox.EventHub.Tests/ \
  && dotnet test tests/Outbox.Store.Tests/
```

Integration tests (after Task 5):

```bash
dotnet test tests/Outbox.IntegrationTests/      # ~3 minutes, requires Docker
```

---

## Task 1: Schema and query updates — SQL Server

The schema change has to land in lockstep with the query change because the `FetchBatch` projection currently SELECTs `o.EventOrdinal`. Update both files in one task to keep the SQL Server build internally consistent.

**Files:**
- Modify: `src/Outbox.SqlServer/db_scripts/install.sql`
- Modify: `src/Outbox.SqlServer/db_scripts/maintenance/partition_switch_cleanup.sql`
- Modify: `src/Outbox.SqlServer/SqlServerQueries.cs`

- [ ] **Step 1: Drop `EventOrdinal` from `dbo.Outbox`**

In `src/Outbox.SqlServer/db_scripts/install.sql`, find the `CREATE TABLE dbo.Outbox` block. Delete this line:

```sql
        EventOrdinal     INT                   NOT NULL  DEFAULT 0,
```

- [ ] **Step 2: Drop `EventOrdinal` from `dbo.OutboxDeadLetter`**

In the same file, in the `CREATE TABLE dbo.OutboxDeadLetter` block, delete this line:

```sql
        EventOrdinal      INT                   NOT NULL  DEFAULT 0,
```

- [ ] **Step 3: Rebuild `IX_Outbox_Pending` on `(PartitionId, SequenceNumber)`**

Replace:

```sql
    CREATE NONCLUSTERED INDEX IX_Outbox_Pending
    ON dbo.Outbox (PartitionId, EventDateTimeUtc, EventOrdinal)
    INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, CreatedAtUtc, RowVersion);
```

with:

```sql
    CREATE NONCLUSTERED INDEX IX_Outbox_Pending
    ON dbo.Outbox (PartitionId, SequenceNumber)
    INCLUDE (TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, EventDateTimeUtc, CreatedAtUtc, RowVersion);
```

Note: `SequenceNumber` is now in the index key, so it's removed from the INCLUDE list. `EventDateTimeUtc` is added to the INCLUDE list since the diagnostic view still reads it.

- [ ] **Step 4: Remove `EventOrdinal` from view definitions**

The current `vw_Outbox` SELECT (around line 137) does not include `EventOrdinal`, and `vw_OutboxDeadLetter` does not include it either — verify by reading the file. If either view does include `EventOrdinal`, drop the column from the SELECT list. **If neither view includes it, no change here.**

- [ ] **Step 5: Update the maintenance partition-switch staging table**

In `src/Outbox.SqlServer/db_scripts/maintenance/partition_switch_cleanup.sql`, find the staging `CREATE TABLE` (around line 72) and delete this line:

```sql
    EventOrdinal       INT                   NOT NULL  DEFAULT 0,
```

Then find the staging index definition (around line 85) and replace:

```sql
CREATE NONCLUSTERED INDEX IX_' + @StagingTable + N'_Pending
ON ' + @StagingFull + N' (PartitionId, EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, CreatedAtUtc, RowVersion);';
```

with:

```sql
CREATE NONCLUSTERED INDEX IX_' + @StagingTable + N'_Pending
ON ' + @StagingFull + N' (PartitionId, SequenceNumber)
INCLUDE (TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, EventDateTimeUtc, CreatedAtUtc, RowVersion);';
```

- [ ] **Step 6: Update `SqlServerQueries.FetchBatch`**

In `src/Outbox.SqlServer/SqlServerQueries.cs`, replace the `FetchBatch` assignment with:

```csharp
        FetchBatch = $"""
                     SELECT TOP (@BatchSize)
                         o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType,
                         o.Headers, o.Payload, o.PayloadContentType,
                         o.EventDateTimeUtc,
                         o.CreatedAtUtc
                     FROM {outboxTable} o WITH (NOLOCK)
                     WHERE o.PartitionId IN (
                         SELECT op.PartitionId
                         FROM {partitionsTable} op
                         WHERE op.OutboxTableName = @OutboxTableName
                           AND op.OwnerPublisherId = @PublisherId
                           AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
                     )
                       AND o.RowVersion < MIN_ACTIVE_ROWVERSION()
                     ORDER BY o.PartitionId, o.SequenceNumber;
                     """;
```

Changes from the current form:
- Removed `o.EventOrdinal` from the SELECT projection.
- `ORDER BY` is now `o.PartitionId, o.SequenceNumber` instead of `o.PartitionId, o.EventDateTimeUtc, o.EventOrdinal, o.SequenceNumber`.
- The `WITH (NOLOCK)` hint and the `RowVersion < MIN_ACTIVE_ROWVERSION()` ceiling filter are unchanged.
- `EventDateTimeUtc` is still selected because the `OutboxMessage` record still carries it.

- [ ] **Step 7: Update `SqlServerQueries.DeadLetter`**

Find the `DeadLetter = $"""..."""` assignment. Drop `deleted.EventOrdinal` from the OUTPUT clause and `EventOrdinal` from the INTO column list. The new form:

```csharp
        DeadLetter = $"""
                      DELETE o
                      OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
                             deleted.EventType, deleted.Headers, deleted.Payload,
                             deleted.PayloadContentType,
                             deleted.CreatedAtUtc, @AttemptCount,
                             deleted.EventDateTimeUtc,
                             SYSUTCDATETIME(), @LastError
                      INTO {deadLetterTable}(SequenceNumber, TopicName, PartitionKey, EventType,
                           Headers, Payload, PayloadContentType,
                           CreatedAtUtc, AttemptCount,
                           EventDateTimeUtc,
                           DeadLetteredAtUtc, LastError)
                      FROM {outboxTable} o
                      INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber;
                      """;
```

- [ ] **Step 8: Update `SqlServerQueries.DeadLetterGet`**

Find `DeadLetterGet = $"""..."""`. Drop `EventOrdinal` from the SELECT list:

```csharp
        DeadLetterGet = $"""
                         SELECT
                             DeadLetterSeq,
                             SequenceNumber,
                             TopicName,
                             PartitionKey,
                             EventType,
                             Headers,
                             Payload,
                             PayloadContentType,
                             EventDateTimeUtc,
                             AttemptCount,
                             CreatedAtUtc,
                             DeadLetteredAtUtc,
                             LastError
                         FROM {deadLetterTable}
                         ORDER BY DeadLetterSeq
                         OFFSET @Offset ROWS
                         FETCH NEXT @Limit ROWS ONLY;
                         """;
```

- [ ] **Step 9: Update `SqlServerQueries.DeadLetterReplay`**

Find `DeadLetterReplay = $"""..."""`. Drop `deleted.EventOrdinal` from the OUTPUT clause and `EventOrdinal` from the INTO column list:

```csharp
        DeadLetterReplay = $"""
                            DELETE dl
                            OUTPUT deleted.TopicName, deleted.PartitionKey, deleted.EventType,
                                   deleted.Headers, deleted.Payload,
                                   deleted.PayloadContentType,
                                   deleted.CreatedAtUtc,
                                   deleted.EventDateTimeUtc
                            INTO {outboxTable}(TopicName, PartitionKey, EventType,
                                 Headers, Payload, PayloadContentType,
                                 CreatedAtUtc,
                                 EventDateTimeUtc)
                            FROM {deadLetterTable} dl
                            INNER JOIN @Ids p ON dl.DeadLetterSeq = p.SequenceNumber;
                            """;
```

- [ ] **Step 10: Update `FetchBatchOutputRow.cs` for SQL Server**

In `src/Outbox.SqlServer/FetchBatchOutputRow.cs`, drop the `EventOrdinal` property and remove it from the `ToDomain()` call:

```csharp
// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.SqlServer;

internal sealed class FetchBatchOutputRow
{
    public long SequenceNumber { get; set; }
    public string TopicName { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Dictionary<string, string>? Headers { get; set; }
    public byte[] Payload { get; set; } = [];
    public string PayloadContentType { get; set; } = string.Empty;
    public DateTimeOffset EventDateTimeUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public OutboxMessage ToDomain() =>
        new(
            SequenceNumber,
            TopicName,
            PartitionKey,
            EventType,
            Headers,
            Payload,
            PayloadContentType,
            EventDateTimeUtc,
            CreatedAtUtc);
}
```

Note: this assumes Task 2 (drop `EventOrdinal` from `OutboxMessage`) is done first. **Order Task 2 before Task 1 if you build between tasks**, OR do Task 1 and Task 2 in the same commit. Either way, do not run `dotnet build` until both are applied — the intermediate state has a constructor mismatch.

The simplest approach: **fold Task 2 into Task 1's commit**. The plan keeps them as separate task descriptions for clarity, but the implementer should treat them as one atomic edit on the C# side.

- [ ] **Step 11: Verify the SQL files**

```bash
grep -n "EventOrdinal\|event_ordinal" src/Outbox.SqlServer/db_scripts/install.sql src/Outbox.SqlServer/db_scripts/maintenance/partition_switch_cleanup.sql src/Outbox.SqlServer/SqlServerQueries.cs
```

Expected: zero matches.

- [ ] **Step 12: Hold off on commit until Task 2 is applied**

Do not commit yet — Task 2 modifies `OutboxMessage`, which `FetchBatchOutputRow.ToDomain()` now references. Both must land in the same commit (or in a tight pair) to keep `dotnet build` runnable. Proceed to Task 2.

---

## Task 2: Drop `EventOrdinal` from the core models

**Files:**
- Modify: `src/Outbox.Core/Models/OutboxMessage.cs`
- Modify: `src/Outbox.Core/Models/OutboxMessageContext.cs`
- Modify: `src/Outbox.Core/Models/DeadLetteredMessage.cs`

- [ ] **Step 1: Replace `OutboxMessage.cs`**

```csharp
// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Represents a message stored in the transactional outbox table.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ordering contract:</b> Messages sharing the same <see cref="PartitionKey" /> are
///         delivered to the broker in <see cref="SequenceNumber" /> order, which equals the
///         order in which they were INSERTed into the outbox table. <b>Callers MUST insert
///         messages in the order they want them delivered.</b> The publisher fetches in
///         <c>sequence_number</c> order within each partition; there is no caller-side override.
///     </para>
///
///     <para>
///         <b>EventDateTimeUtc:</b> Caller-asserted business timestamp. Stored on the row and
///         carried through to the dead-letter table for forensics. <b>Not consulted by the
///         publisher's sort path</b> — it exists for debugging and observability only.
///     </para>
///
///     <para>
///         <b>At-least-once guarantee:</b> Messages may be delivered more than once.
///         Consumers must be idempotent. Consider using <see cref="SequenceNumber" /> as a
///         deduplication key on the consumer side.
///     </para>
///
///     <para>
///         <b>Retry tracking:</b> Retry state is held in process memory by the publisher's
///         in-batch retry loop. There is no persistent retry counter on this record; restarts
///         re-fetch failed messages with a fresh attempt budget.
///     </para>
/// </remarks>
public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    Dictionary<string, string>? Headers,
    byte[] Payload,
    string PayloadContentType,
    DateTimeOffset EventDateTimeUtc,
    DateTimeOffset CreatedAtUtc);
```

The change versus the current file is the removal of the `int EventOrdinal` parameter and the rewriting of the doc remarks to describe the new contract.

- [ ] **Step 2: Update `OutboxMessageContext.cs`**

If the file currently has an accessor like `public int EventOrdinal => _original.EventOrdinal;`, delete that line. Read the file first and verify which lines need to go.

- [ ] **Step 3: Update `DeadLetteredMessage.cs`**

Find the record declaration. Delete the `int EventOrdinal,` parameter (it's after `EventDateTimeUtc`). Update the parameterless constructor's positional list to match — drop the corresponding `0` argument.

The new form (verify against the current file):

```csharp
// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

public sealed record DeadLetteredMessage(
    long DeadLetterSeq,
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    Dictionary<string, string>? Headers,
    byte[] Payload,
    string PayloadContentType,
    DateTimeOffset EventDateTimeUtc,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadLetteredAtUtc,
    string? LastError)
{
    public DeadLetteredMessage() : this(0, 0, "", "", "", null, [], "", default, 0, default, default, null) { }
}
```

- [ ] **Step 4: Update `OutboxPublisherService.ProcessGroupWithRetriesAsync` sort**

In `src/Outbox.Core/Engine/OutboxPublisherService.cs`, find this block (around line 365):

```csharp
        var remaining = initialGroup
            .OrderBy(m => m.EventDateTimeUtc)
            .ThenBy(m => m.EventOrdinal)
            .ThenBy(m => m.SequenceNumber)
            .ToList();
```

Replace with:

```csharp
        var remaining = initialGroup
            .OrderBy(m => m.SequenceNumber)
            .ToList();
```

The store query already returns rows in `sequence_number` order within a partition; this in-memory re-sort is now strictly redundant, but keeping the explicit `OrderBy(SequenceNumber)` makes the contract visible at the call site without depending on caller assumptions about row order.

- [ ] **Step 5: Build the `src/` tree**

```bash
dotnet build src/Outbox.slnx 2>&1 | tail -10
```

Expected: SQL Server project builds. PostgreSQL project will FAIL because its own `FetchBatchOutputRow` and queries still reference `EventOrdinal` / `event_ordinal` and the new `OutboxMessage` constructor doesn't accept it. **That's expected — Task 3 fixes PostgreSQL.** Do not commit until Task 3 is done.

---

## Task 3: Schema and query updates — PostgreSQL

Symmetric to Task 1 but for PostgreSQL. The intermediate state from Task 2 leaves PostgreSQL broken; this task fixes it.

**Files:**
- Modify: `src/Outbox.PostgreSQL/db_scripts/install.sql`
- Modify: `src/Outbox.PostgreSQL/PostgreSqlQueries.cs`
- Modify: `src/Outbox.PostgreSQL/FetchBatchOutputRow.cs`

- [ ] **Step 1: Drop `event_ordinal` from `outbox` table**

In `src/Outbox.PostgreSQL/db_scripts/install.sql`, find the `CREATE TABLE IF NOT EXISTS outbox` block. Delete this line:

```sql
    event_ordinal      INT            NOT NULL DEFAULT 0,
```

- [ ] **Step 2: Drop `event_ordinal` from `outbox_dead_letter` table**

Same file, in `CREATE TABLE IF NOT EXISTS outbox_dead_letter`. Delete:

```sql
    event_ordinal        INT            NOT NULL DEFAULT 0,
```

- [ ] **Step 3: Rebuild `ix_outbox_pending` on `sequence_number`**

Replace:

```sql
CREATE INDEX IF NOT EXISTS ix_outbox_pending
ON outbox (event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, created_at_utc);
```

with:

```sql
CREATE INDEX IF NOT EXISTS ix_outbox_pending
ON outbox (sequence_number)
INCLUDE (topic_name, partition_key, event_type, event_datetime_utc, created_at_utc);
```

`sequence_number` is now in the index key, so it leaves the INCLUDE list. `event_datetime_utc` joins the INCLUDE list since the diagnostic view still references it.

- [ ] **Step 4: Verify PostgreSQL diagnostic views**

The current `vw_outbox` SELECT (around line 95) does not include `event_ordinal`. Verify by reading the file. If it does include it, drop it. Same for `vw_outbox_dead_letter`.

- [ ] **Step 5: Update `PostgreSqlQueries.FetchBatch`**

In `src/Outbox.PostgreSQL/PostgreSqlQueries.cs`, replace the `FetchBatch` assignment with:

```csharp
        FetchBatch = $@"
SELECT o.sequence_number, o.topic_name, o.partition_key, o.event_type,
       o.headers, o.payload, o.payload_content_type,
       o.event_datetime_utc,
       o.created_at_utc
FROM {outboxTable} o
INNER JOIN {partitionsTable} op
    ON  op.outbox_table_name = @outbox_table_name
    AND op.owner_publisher_id = @publisher_id
    AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < clock_timestamp())
    AND ((hashtext(o.partition_key) & 2147483647) % @total_partitions) = op.partition_id
WHERE o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
ORDER BY o.sequence_number
LIMIT @batch_size;";
```

Changes from the current form:
- Dropped `o.event_ordinal` from the projection.
- `ORDER BY` is now just `o.sequence_number` instead of `o.event_datetime_utc, o.event_ordinal, o.sequence_number`.
- The `xmin` ceiling filter and the partition-id join are unchanged.

- [ ] **Step 6: Update `PostgreSqlQueries.DeadLetter`**

Replace with:

```csharp
        DeadLetter = $@"
WITH dead AS (
    DELETE FROM {outboxTable}
    WHERE  sequence_number = ANY(@ids)
    RETURNING sequence_number, topic_name, partition_key, event_type,
              headers, payload, payload_content_type,
              created_at_utc,
              event_datetime_utc
)
INSERT INTO {deadLetterTable}
    (sequence_number, topic_name, partition_key, event_type, headers, payload,
     payload_content_type,
     created_at_utc, attempt_count, event_datetime_utc,
     dead_lettered_at_utc, last_error)
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       payload_content_type,
       created_at_utc, @attempt_count, event_datetime_utc,
       clock_timestamp(), @last_error
FROM dead;";
```

Removed `event_ordinal` from the CTE RETURNING, the INSERT column list, and the SELECT projection.

- [ ] **Step 7: Update `PostgreSqlQueries.DeadLetterGet`**

Replace with:

```csharp
        DeadLetterGet = $@"
SELECT dead_letter_seq, sequence_number, topic_name, partition_key, event_type, headers, payload,
       payload_content_type,
       event_datetime_utc, attempt_count, created_at_utc,
       dead_lettered_at_utc, last_error
FROM   {deadLetterTable}
ORDER  BY dead_letter_seq
LIMIT  @limit OFFSET @offset;";
```

`event_ordinal` removed from the SELECT.

- [ ] **Step 8: Update `PostgreSqlQueries.DeadLetterReplay`**

Replace with:

```csharp
        DeadLetterReplay = $@"
WITH replayed AS (
    DELETE FROM {deadLetterTable}
    WHERE  dead_letter_seq = ANY(@ids)
    RETURNING sequence_number, topic_name, partition_key, event_type, headers, payload,
              payload_content_type,
              created_at_utc, event_datetime_utc
)
INSERT INTO {outboxTable}
    (topic_name, partition_key, event_type, headers, payload,
     payload_content_type,
     created_at_utc, event_datetime_utc)
SELECT topic_name, partition_key, event_type, headers, payload,
       payload_content_type,
       created_at_utc, event_datetime_utc
FROM replayed;";
```

`event_ordinal` removed from CTE RETURNING, INSERT column list, and SELECT.

- [ ] **Step 9: Update `FetchBatchOutputRow.cs` for PostgreSQL**

Replace `src/Outbox.PostgreSQL/FetchBatchOutputRow.cs` with:

```csharp
// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.PostgreSQL;

internal sealed class FetchBatchOutputRow
{
    public long SequenceNumber { get; set; }
    public string TopicName { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Dictionary<string, string>? Headers { get; set; }
    public byte[] Payload { get; set; } = [];
    public string PayloadContentType { get; set; } = string.Empty;
    public DateTimeOffset EventDateTimeUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public OutboxMessage ToDomain() =>
        new(
            SequenceNumber,
            TopicName,
            PartitionKey,
            EventType,
            Headers,
            Payload,
            PayloadContentType,
            EventDateTimeUtc,
            CreatedAtUtc);
}
```

- [ ] **Step 10: Verify SQL files**

```bash
grep -n "event_ordinal\|EventOrdinal" \
    src/Outbox.PostgreSQL/db_scripts/install.sql \
    src/Outbox.PostgreSQL/PostgreSqlQueries.cs \
    src/Outbox.PostgreSQL/FetchBatchOutputRow.cs
```

Expected: zero matches.

- [ ] **Step 11: Build the full `src/` tree**

```bash
dotnet build src/Outbox.slnx 2>&1 | tail -10
```

Expected: all `src/*` projects build cleanly. Test projects will still fail (Task 4 fixes them).

- [ ] **Step 12: Commit Tasks 1, 2, 3 together**

```bash
git add src/Outbox.SqlServer/db_scripts/install.sql \
        src/Outbox.SqlServer/db_scripts/maintenance/partition_switch_cleanup.sql \
        src/Outbox.SqlServer/SqlServerQueries.cs \
        src/Outbox.SqlServer/FetchBatchOutputRow.cs \
        src/Outbox.PostgreSQL/db_scripts/install.sql \
        src/Outbox.PostgreSQL/PostgreSqlQueries.cs \
        src/Outbox.PostgreSQL/FetchBatchOutputRow.cs \
        src/Outbox.Core/Models/OutboxMessage.cs \
        src/Outbox.Core/Models/OutboxMessageContext.cs \
        src/Outbox.Core/Models/DeadLetteredMessage.cs \
        src/Outbox.Core/Engine/OutboxPublisherService.cs
git commit -m "feat(outbox): order by sequence_number, drop event_ordinal column"
```

The single commit keeps the source tree internally consistent at every checkpoint.

---

## Task 4: Update unit tests

Sweep through `tests/Outbox.Core.Tests/`, `tests/Outbox.Kafka.Tests/`, `tests/Outbox.EventHub.Tests/`, and `tests/Outbox.PerformanceTests/` to drop the `EventOrdinal` references that the build now rejects.

**Files (find via grep):**

```bash
grep -rn "EventOrdinal\|event_ordinal" tests/ --include="*.cs" | head -30
```

Common patterns to fix:

- `OutboxMessage` constructor calls with positional or named `EventOrdinal` argument → drop it.
- `TestOutboxServiceFactory.MakeMessage(... eventOrdinal: ...)` → drop the parameter and the corresponding default in the factory definition.
- `Substitute.For<...>().Returns` chains that fabricate messages with an `EventOrdinal` value → drop it.
- Assertions on `.EventOrdinal` (e.g., `Assert.Equal(0, msg.EventOrdinal)`) → delete the assertion.
- Tests in `OutboxMessageContextTests.cs` that exercise the now-removed `EventOrdinal` accessor → delete those tests.
- `ModelTests.cs` tests that exercise the `OutboxMessage` or `DeadLetteredMessage` shape → adjust the constructor calls and drop any `EventOrdinal`-specific assertions.

- [ ] **Step 1: Update `TestOutboxServiceFactory.MakeMessage`**

Find the `MakeMessage` static method in `tests/Outbox.Core.Tests/TestOutboxServiceFactory.cs`. The current signature includes `int eventOrdinal = 0`. Drop that parameter and drop the corresponding positional argument from the `OutboxMessage` constructor call.

After:

```csharp
    public static OutboxMessage MakeMessage(
        long seq, string topic = "orders", string key = "key-1",
        DateTimeOffset? eventTime = null) =>
        new(seq, topic, key, "OrderCreated", null,
            System.Text.Encoding.UTF8.GetBytes("{}"), "application/json",
            eventTime ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
```

Any caller passing `eventOrdinal:` will now break — that's intentional, you'll fix them in the next step.

- [ ] **Step 2: Sweep all test files**

For each file in the grep output, apply the rules above. Build incrementally:

```bash
dotnet build tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj 2>&1 | grep "error" | head -20
```

Iterate until zero errors. Repeat for `Outbox.Kafka.Tests`, `Outbox.EventHub.Tests`, `Outbox.PerformanceTests`.

- [ ] **Step 3: Update `OrderingContractTests`**

`tests/Outbox.Core.Tests/Behavioral/OrderingContractTests.cs` is the load-bearing test for the ordering contract. Read it carefully. Any test that:
- Asserts `EventDateTimeUtc` is the primary sort key → reword the assertion. The new contract is "sequence_number is the sort key".
- Constructs messages with intentionally out-of-order `EventDateTimeUtc` values to verify the publisher reorders them → these tests should now assert that the publisher delivers them in `SequenceNumber` order instead.
- Uses `EventOrdinal` as a tiebreaker → drop those assertions; sequence numbers are now the only ordering signal.

If a test no longer makes sense under the new contract, delete it and add a comment in the commit message about which test was removed and why. The new contract is much simpler — most tests should collapse, not grow.

- [ ] **Step 4: Run all unit tests**

```bash
dotnet test tests/Outbox.Core.Tests/ \
  && dotnet test tests/Outbox.Kafka.Tests/ \
  && dotnet test tests/Outbox.EventHub.Tests/ \
  && dotnet test tests/Outbox.Store.Tests/
```

Expected: all green. If anything fails at runtime, investigate per failure — most likely a test fixture is still constructing messages with the dropped field, or asserting something about the old ordering contract.

- [ ] **Step 5: Commit**

```bash
git add tests/Outbox.Core.Tests/ tests/Outbox.Kafka.Tests/ tests/Outbox.EventHub.Tests/ tests/Outbox.PerformanceTests/
git commit -m "test(outbox): drop EventOrdinal from unit tests after sort-key change"
```

---

## Task 5: Update integration tests

The integration tests run against real PostgreSQL and SQL Server containers via Testcontainers. They directly INSERT into and SELECT from the outbox table, so dropping the `event_ordinal` column hits them.

**Files:**
- `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs` (PostgreSQL)
- `tests/Outbox.IntegrationTests/Helpers/SqlServerTestHelper.cs`
- `tests/Outbox.IntegrationTests/Scenarios/PoisonMessageTests.cs` and `SqlServer/SqlServerPoisonMessageTests.cs`
- `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs` and `SqlServer/SqlServerConcurrentTransactionOrderingTests.cs`
- `tests/Outbox.IntegrationTests/Scenarios/RebalanceOrderingTests.cs`
- Any other scenario file the grep identifies.

- [ ] **Step 1: Find affected files**

```bash
grep -rn "event_ordinal\|EventOrdinal" tests/Outbox.IntegrationTests/ --include="*.cs"
```

- [ ] **Step 2: Update fixture INSERT statements**

For each helper file, find the SQL INSERT statements that seed outbox rows. Drop `event_ordinal` (PostgreSQL) or `EventOrdinal` (SQL Server) from the column list and from the values list. Same for any direct INSERT in scenario tests.

- [ ] **Step 3: Update fixture SELECT statements**

Drop reads of `event_ordinal` / `EventOrdinal` from any SELECT that pulls outbox rows back for assertions. The C# materialization target (`OutboxMessage`) no longer has the field.

- [ ] **Step 4: Review ordering-sensitive scenarios**

`ConcurrentTransactionOrderingTests` and `RebalanceOrderingTests` are the load-bearing ordering tests. Review their assertions:
- If they assert delivery order matches the `event_datetime_utc` value the test fixture set, that's no longer the contract. Update them to assert delivery matches insert order (which equals `sequence_number` order).
- If they construct fixtures with intentionally non-monotonic `event_datetime_utc` values to test the old caller-asserted-order contract, they're now testing the wrong thing. Either delete them (the new contract is "callers insert in delivery order" and there's nothing to test about caller-asserted order other than that the column exists for forensics) or rewrite them to construct fixtures in the order they expect delivery and assert that order.

- [ ] **Step 5: Build the integration test project**

```bash
dotnet build tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj 2>&1 | tail -10
```

Expected: 0 errors.

- [ ] **Step 6: Run integration tests (requires Docker)**

```bash
dotnet test tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj
```

Expected: ~3 minutes, all tests pass. Ordering and rebalance scenarios are the most likely to surface real failures — investigate them carefully if they're red. The most common cause will be a test that expected `event_datetime_utc`-based ordering rather than `sequence_number`-based ordering.

- [ ] **Step 7: Commit**

```bash
git add tests/Outbox.IntegrationTests/
git commit -m "test(integration): drop event_ordinal and update ordering assertions"
```

---

## Task 6: Update documentation

The ordering contract is referenced in many docs. Update them all in one pass.

**Files:**
- `docs/outbox-requirements-invariants.md`
- `docs/architecture.md`
- `docs/publisher-flow.md`
- `docs/known-limitations.md`
- `docs/failure-scenarios-and-integration-tests.md`
- `docs/production-runbook.md`
- `docs/sqlserver-eventhub-publisher-reference.md`
- `src/Outbox.Core/README.md`
- `src/Outbox.SqlServer/README.md`
- `src/Outbox.PostgreSQL/README.md`
- `README.md` (top-level, if it documents the contract)
- `CLAUDE.md`
- `docs/sqlserver-query-plan-report.md` (historical — see special note below)
- `docs/ordering-column-design-note.md` (mark as accepted)

### Specific edits

- [ ] **Step 1: `docs/outbox-requirements-invariants.md`**

Find the **Message ordering** section. Replace the bullets with:

```markdown
### Message ordering

- **Within a (topic, partitionKey) group in a single batch:** messages MUST be sent to the broker in `sequence_number` order. Since `sequence_number` is `BIGINT IDENTITY` (SQL Server) / `bigserial` (PostgreSQL), this equals the order in which the rows were INSERTed.
- **Callers MUST insert messages in the order they want them delivered.** There is no caller-side override. `event_datetime_utc` is recorded for forensics but does NOT influence delivery order.
- **Across batches for the same partitionKey:** ordering is guaranteed by the SQL `ORDER BY sequence_number` in FetchBatch, combined with a version ceiling filter (`xmin < pg_snapshot_xmin(pg_current_snapshot())` on PostgreSQL, `RowVersion < MIN_ACTIVE_ROWVERSION()` on SQL Server) that withholds freshly inserted rows when earlier write transactions are still in-flight. This prevents the scenario where Transaction #2 commits before Transaction #1 and its rows are published out of order. Trade-off: any concurrent write transaction in the database temporarily pauses processing of new inserts until it commits.
- **Cross-partitionKey:** no ordering guarantee. This is by design.
```

In the **Store Contract** section, update item #1 (`FetchBatch`):

```markdown
1. **FetchBatch:** MUST return messages ordered by `sequence_number`, scoped per partition. MUST apply the version ceiling filter (`xmin < pg_snapshot_xmin(pg_current_snapshot())` on PostgreSQL, `RowVersion < MIN_ACTIVE_ROWVERSION()` on SQL Server) to withhold rows from in-flight write transactions. MUST only return messages for partitions owned by the requesting publisher (with grace period check). MUST return `payload_content_type` alongside `headers`/`payload`. MUST deserialize `headers` from JSON text to `Dictionary<string, string>?`. This is a pure SELECT — no row locking or UPDATE is performed. **SQL Server: the query MUST use `WITH (NOLOCK)` on the outbox table.** Without it, `READ COMMITTED` takes shared locks during scanning and blocks on rows with exclusive locks from uncommitted transactions, causing timeouts. `NOLOCK` and `MIN_ACTIVE_ROWVERSION()` work together by design: `NOLOCK` prevents reader blocking on writer locks, while `MIN_ACTIVE_ROWVERSION()` filters out uncommitted rows by their row version. Do not remove `NOLOCK` — it is required for non-blocking reads.
```

In the **Anti-Patterns to Watch For** section, find the anti-pattern about modifying `FetchBatch ORDER BY` (mentioned in `CLAUDE.md`'s review checklist). Update it to reflect that the new sort is `sequence_number`, not the old composite key.

- [ ] **Step 2: `CLAUDE.md`**

Find the review checklist line about `FetchBatch ORDER BY`:

```markdown
- [ ] **MESSAGE ORDERING MUST NEVER BE CORRUPTED.** Per-(topic, partitionKey) ordering is the core guarantee. Any change that could cause two publishers to process the same partition key simultaneously, or reorder messages within a partition key, is a critical bug. This includes: changing partition counts while publishers are running, changing the hash function, modifying FetchBatch ORDER BY, or breaking the single-writer-per-partition invariant.
```

Replace `modifying FetchBatch ORDER BY` (the order is now `sequence_number`, and changing it would break things again) with text that clarifies what's protected:

```markdown
- [ ] **MESSAGE ORDERING MUST NEVER BE CORRUPTED.** Per-(topic, partitionKey) ordering is the core guarantee. Any change that could cause two publishers to process the same partition key simultaneously, or reorder messages within a partition key, is a critical bug. This includes: changing partition counts while publishers are running, changing the hash function, modifying the FetchBatch `ORDER BY sequence_number` clause, breaking the "callers insert in delivery order" stipulation, or breaking the single-writer-per-partition invariant.
```

- [ ] **Step 3: `docs/architecture.md`, `docs/publisher-flow.md`**

Grep these files for `event_datetime_utc, event_ordinal` and update the explanations of the sort key. Wherever the docs say "messages are ordered by event timestamp," replace with "messages are ordered by `sequence_number`, which equals insert order." Keep mentions of `event_datetime_utc` as a debug/forensics column.

- [ ] **Step 4: `docs/known-limitations.md`**

Grep for `event_ordinal`. If anything mentions the column or the caller-asserted-order capability, update it to describe the new contract: callers must insert in delivery order; there's no in-database reordering.

- [ ] **Step 5: `docs/failure-scenarios-and-integration-tests.md`**

Grep for `event_datetime_utc, event_ordinal`. Update any scenario that mentions the old ordering contract.

- [ ] **Step 6: `docs/production-runbook.md`**

Grep for `event_ordinal`. If the runbook contains ops queries that SELECT `event_ordinal`, drop the column from those queries. The column doesn't exist anymore.

- [ ] **Step 7: `docs/sqlserver-eventhub-publisher-reference.md`**

Grep and update.

- [ ] **Step 8: Per-store and Core READMEs**

`src/Outbox.Core/README.md`, `src/Outbox.SqlServer/README.md`, `src/Outbox.PostgreSQL/README.md`. Drop `event_ordinal` / `EventOrdinal` from any column listings or schema diagrams. Update any text that describes the ordering contract.

- [ ] **Step 9: `docs/sqlserver-query-plan-report.md`** (historical doc)

This is a historical performance report capturing the old index. Don't rewrite the historical content. Add a note at the top:

```markdown
> **Historical note (2026-04-XX):** The index `IX_Outbox_Pending` referenced in this report has been rebuilt on `(PartitionId, SequenceNumber)` as part of the sequence-number ordering change. The query plans shown below reflect the previous `(PartitionId, EventDateTimeUtc, EventOrdinal)` index and may not match current behavior. A re-baseline run is pending.
```

- [ ] **Step 10: `docs/ordering-column-design-note.md`**

This is the document that originally proposed the change. Mark it as accepted — add at the top, immediately after the existing **Status** line:

```markdown
**Status update (2026-04-XX):** Accepted and implemented in the sequence-number ordering change. See `docs/sequence-number-ordering-implementation-plan.md` for the implementation plan and the resulting commits.
```

- [ ] **Step 11: Verification grep**

```bash
grep -rn "event_ordinal\|EventOrdinal" docs/ src/Outbox.Core/README.md src/Outbox.SqlServer/README.md src/Outbox.PostgreSQL/README.md README.md CLAUDE.md
```

Expected: matches only in:
- `docs/sqlserver-query-plan-report.md` (historical, with the note added in Step 9)
- `docs/ordering-column-design-note.md` (the design note itself, which is a historical analysis document)
- `docs/sequence-number-ordering-implementation-plan.md` (this plan)
- Anywhere the docs intentionally describe the OLD model in a "from → to" comparison

Anything else is a stale reference — fix it.

- [ ] **Step 12: Commit**

```bash
git add docs/ CLAUDE.md src/Outbox.Core/README.md src/Outbox.SqlServer/README.md src/Outbox.PostgreSQL/README.md README.md
git commit -m "docs(outbox): update ordering contract for sequence_number sort key"
```

---

## Task 7: Final verification sweep

- [ ] **Step 1: Full solution build**

```bash
dotnet build src/Outbox.slnx
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: All unit tests**

```bash
dotnet test tests/Outbox.Core.Tests/ \
  && dotnet test tests/Outbox.Kafka.Tests/ \
  && dotnet test tests/Outbox.EventHub.Tests/ \
  && dotnet test tests/Outbox.Store.Tests/
```

Expected: all green.

- [ ] **Step 3: Integration tests**

```bash
dotnet test tests/Outbox.IntegrationTests/
```

Expected: all green. ~3 minutes, requires Docker.

- [ ] **Step 4: Repo-wide grep for stale references**

```bash
grep -rn "EventOrdinal\|event_ordinal" \
    --include="*.cs" --include="*.sql" --include="*.md" \
    src/ tests/ docs/ CLAUDE.md README.md
```

Expected: matches only in the three explicitly-allowed files (the design note, the historical query plan report, and this plan). Any other match is a stale reference.

- [ ] **Step 5: Repo-wide grep for stale `ORDER BY` references**

```bash
grep -rn "ORDER BY.*event_datetime_utc\|ORDER BY.*EventDateTimeUtc" src/ tests/ docs/
```

Expected: zero matches in `src/`. Test fixtures may legitimately query the dead-letter table by event time for forensics — that's fine. Doc references should already be cleaned in Task 6.

- [ ] **Step 6: Manual perf re-baseline (out of scope for the agent)**

The 60-minute perf suite needs to be re-run. The new index `(PartitionId, SequenceNumber)` is narrower than the old composite key — expect slightly faster fetch on SQL Server because the index is smaller and the sort is on the leading column of the cluster key. PostgreSQL's BTREE on `sequence_number` should be similarly tight. Update `docs/postgresql-performance-results.md` and `docs/sqlserver-performance-results.md` after the run.

- [ ] **Step 7: No commit (read-only verification)**

If everything is green, the change is complete.

---

## Spec coverage check

| Spec section / requirement | Implementing task |
|---|---|
| Drop `event_ordinal` column from SQL Server outbox table | Task 1 (Step 1) |
| Drop `event_ordinal` column from SQL Server DLQ table | Task 1 (Step 2) |
| Rebuild `IX_Outbox_Pending` keyed on `(PartitionId, SequenceNumber)` | Task 1 (Step 3) |
| Drop `event_ordinal` column from PostgreSQL outbox table | Task 3 (Step 1) |
| Drop `event_ordinal` column from PostgreSQL DLQ table | Task 3 (Step 2) |
| Rebuild `ix_outbox_pending` keyed on `sequence_number` | Task 3 (Step 3) |
| Update SQL Server `FetchBatch` query | Task 1 (Step 6) |
| Update PostgreSQL `FetchBatch` query | Task 3 (Step 5) |
| Update SQL Server `DeadLetter`/`DeadLetterGet`/`DeadLetterReplay` queries | Task 1 (Steps 7, 8, 9) |
| Update PostgreSQL `DeadLetter`/`DeadLetterGet`/`DeadLetterReplay` queries | Task 3 (Steps 6, 7, 8) |
| Drop `EventOrdinal` from `OutboxMessage` record | Task 2 (Step 1) |
| Drop `EventOrdinal` accessor from `OutboxMessageContext` | Task 2 (Step 2) |
| Drop `EventOrdinal` from `DeadLetteredMessage` record | Task 2 (Step 3) |
| Update publisher's in-memory sort | Task 2 (Step 4) |
| Drop `EventOrdinal` from `FetchBatchOutputRow` (both stores) | Task 1 (Step 10), Task 3 (Step 9) |
| Update partition switch maintenance script | Task 1 (Step 5) |
| Keep `EventDateTimeUtc` in schema and on the record | Confirmed via the spec-correct content of Task 2 (Step 1) |
| Keep version ceiling filter and `WITH (NOLOCK)` | Confirmed via the spec-correct content of Task 1 (Step 6) |
| Update `TestOutboxServiceFactory.MakeMessage` | Task 4 (Step 1) |
| Update unit tests across Core/Kafka/EventHub/Store/Performance | Task 4 (Steps 2, 3) |
| Update integration test fixtures and ordering tests | Task 5 |
| Update `outbox-requirements-invariants.md` | Task 6 (Step 1) |
| Update `CLAUDE.md` review checklist | Task 6 (Step 2) |
| Update architecture/publisher-flow docs | Task 6 (Step 3) |
| Update known-limitations and failure-scenarios docs | Task 6 (Steps 4, 5) |
| Update production runbook | Task 6 (Step 6) |
| Update per-store and Core READMEs | Task 6 (Step 8) |
| Mark design note as accepted | Task 6 (Step 10) |
| Note historical query plan report | Task 6 (Step 9) |
| Final verification sweep | Task 7 |

Every requirement from the user's decision (drop `event_ordinal`, drop from `ORDER BY`, keep `event_datetime_utc` for forensics, update docs) maps to at least one task.
