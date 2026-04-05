# SQL Server Ghost Record Maintenance — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship 4 maintenance SQL scripts for SQL Server ghost record mitigation, add runbook procedures, and document the limitation.

**Architecture:** Pure SQL scripts in `db_scripts/maintenance/` — no application code changes. All scripts parameterized with `@SchemaName`/`@TablePrefix`. Documentation added to existing runbook and known-limitations docs.

**Tech Stack:** T-SQL, SQL Server DMVs (`sys.dm_db_index_physical_stats`, `sys.dm_db_partition_stats`)

---

### Task 1: Ghost Record Check Script

**Files:**
- Create: `src/Outbox.SqlServer/db_scripts/maintenance/check_ghost_records.sql`

- [ ] **Step 1: Create the maintenance directory**

```bash
mkdir -p src/Outbox.SqlServer/db_scripts/maintenance
```

- [ ] **Step 2: Write `check_ghost_records.sql`**

```sql
-- =============================================================================
-- check_ghost_records.sql
-- Diagnostic: assess ghost record count and index fragmentation on Outbox tables.
-- Requires: VIEW DATABASE STATE permission.
-- Schedule: on-demand or daily via SQL Agent / Azure Elastic Job.
-- =============================================================================

DECLARE @SchemaName SYSNAME = N'dbo';
DECLARE @TablePrefix NVARCHAR(128) = N'';

-- Thresholds (adjust as needed)
DECLARE @FragReorganize FLOAT = 10.0;   -- REORGANIZE above this %
DECLARE @FragRebuild    FLOAT = 30.0;   -- REBUILD above this %
DECLARE @GhostWarn      BIGINT = 1000;  -- REORGANIZE above this count
DECLARE @GhostCritical  BIGINT = 10000; -- REBUILD above this count
DECLARE @SmallTablePages BIGINT = 1000; -- Below this, fragmentation % is unreliable

-- Resolve table names
DECLARE @OutboxTable SYSNAME = @TablePrefix + N'Outbox';
DECLARE @DeadLetterTable SYSNAME = @TablePrefix + N'OutboxDeadLetter';
DECLARE @OutboxFullName NVARCHAR(256) = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@OutboxTable);
DECLARE @DeadLetterFullName NVARCHAR(256) = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@DeadLetterTable);

-- Edition info
DECLARE @EngineEdition INT = CAST(SERVERPROPERTY('EngineEdition') AS INT);
DECLARE @EditionDesc NVARCHAR(50) = CASE @EngineEdition
    WHEN 1 THEN 'Personal/Desktop'
    WHEN 2 THEN 'Standard'
    WHEN 3 THEN 'Enterprise'
    WHEN 4 THEN 'Express'
    WHEN 5 THEN 'Azure SQL Database'
    WHEN 6 THEN 'Azure Synapse'
    WHEN 8 THEN 'Azure SQL Managed Instance'
    ELSE 'Unknown (' + CAST(@EngineEdition AS NVARCHAR(10)) + ')'
END;

PRINT '=== Outbox Ghost Record & Fragmentation Report ===';
PRINT 'Server: ' + @@SERVERNAME;
PRINT 'Edition: ' + @EditionDesc;
PRINT 'Timestamp: ' + CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126) + ' UTC';
PRINT '';

-- Table space summary
SELECT
    QUOTENAME(s.name) + '.' + QUOTENAME(t.name) AS [Table],
    SUM(p.rows) AS [RowCount],
    CAST(SUM(a.total_pages) * 8.0 / 1024 AS DECIMAL(10,2)) AS [TotalSpaceMB],
    CAST(SUM(a.used_pages) * 8.0 / 1024 AS DECIMAL(10,2)) AS [UsedSpaceMB],
    CAST((SUM(a.total_pages) - SUM(a.used_pages)) * 8.0 / 1024 AS DECIMAL(10,2)) AS [UnusedSpaceMB]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE s.name = @SchemaName
  AND t.name IN (@OutboxTable, @DeadLetterTable)
GROUP BY s.name, t.name
ORDER BY t.name;

-- Index health detail
SELECT
    QUOTENAME(s.name) + '.' + QUOTENAME(t.name) AS [Table],
    i.name AS [IndexName],
    i.type_desc AS [IndexType],
    ps.page_count AS [PageCount],
    ps.record_count AS [RecordCount],
    ps.ghost_record_count AS [GhostRecordCount],
    CAST(ps.avg_fragmentation_in_percent AS DECIMAL(5,2)) AS [FragmentationPct],
    CAST(ps.avg_fragment_size_in_pages AS DECIMAL(10,2)) AS [AvgFragmentSizePages],
    CASE
        WHEN ps.page_count < @SmallTablePages AND ps.ghost_record_count > @GhostWarn
            THEN 'INVESTIGATE'
        WHEN ps.avg_fragmentation_in_percent > @FragRebuild OR ps.ghost_record_count > @GhostCritical
            THEN 'REBUILD'
        WHEN ps.avg_fragmentation_in_percent > @FragReorganize OR ps.ghost_record_count > @GhostWarn
            THEN 'REORGANIZE'
        ELSE 'OK'
    END AS [Recommendation]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes i ON t.object_id = i.object_id
CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), t.object_id, i.index_id, NULL, 'LIMITED') ps
WHERE s.name = @SchemaName
  AND t.name IN (@OutboxTable, @DeadLetterTable)
  AND i.type > 0  -- exclude heaps
ORDER BY t.name, i.index_id;
```

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.SqlServer/db_scripts/maintenance/check_ghost_records.sql
git commit -m "feat: add ghost record diagnostic script for SQL Server

Queries sys.dm_db_index_physical_stats in LIMITED mode to report ghost
record count, fragmentation %, and actionable recommendations per index
on the Outbox and OutboxDeadLetter tables."
```

---

### Task 2: Reorganize Indexes Script

**Files:**
- Create: `src/Outbox.SqlServer/db_scripts/maintenance/reorganize_indexes.sql`

- [ ] **Step 1: Write `reorganize_indexes.sql`**

```sql
-- =============================================================================
-- reorganize_indexes.sql
-- Online index defragmentation — removes ghost records and compacts pages.
-- Always online. No edition requirement. Safe during active publishing.
-- Requires: ALTER permission on the Outbox table.
-- Schedule: weekly preventive, or after broker outage recovery.
-- =============================================================================

DECLARE @SchemaName SYSNAME = N'dbo';
DECLARE @TablePrefix NVARCHAR(128) = N'';
DECLARE @IncludeDeadLetter BIT = 0;  -- Set to 1 to also reorganize dead-letter indexes

DECLARE @OutboxTable SYSNAME = @TablePrefix + N'Outbox';
DECLARE @DeadLetterTable SYSNAME = @TablePrefix + N'OutboxDeadLetter';

PRINT '=== Outbox Index Reorganize ===';
PRINT 'Timestamp: ' + CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126) + ' UTC';
PRINT '';

-- ---- Before snapshot ----
PRINT '--- Before Reorganize ---';
SELECT
    QUOTENAME(@SchemaName) + '.' + QUOTENAME(t.name) AS [Table],
    i.name AS [IndexName],
    CAST(ps.avg_fragmentation_in_percent AS DECIMAL(5,2)) AS [FragmentationPct],
    ps.ghost_record_count AS [GhostRecordCount],
    ps.page_count AS [PageCount]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes i ON t.object_id = i.object_id
CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), t.object_id, i.index_id, NULL, 'LIMITED') ps
WHERE s.name = @SchemaName
  AND (t.name = @OutboxTable OR (t.name = @DeadLetterTable AND @IncludeDeadLetter = 1))
  AND i.type > 0
ORDER BY t.name, i.index_id;

-- ---- Reorganize Outbox indexes ----
DECLARE @SQL NVARCHAR(MAX);
DECLARE @StartTime DATETIME2 = SYSUTCDATETIME();

-- Clustered index (PK_Outbox or PK_{Prefix}Outbox)
SET @SQL = N'ALTER INDEX ALL ON ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@OutboxTable) + N' REORGANIZE;';
PRINT 'Executing: ' + @SQL;
EXEC sp_executesql @SQL;

-- Update statistics on Outbox table
SET @SQL = N'UPDATE STATISTICS ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@OutboxTable) + N';';
PRINT 'Executing: ' + @SQL;
EXEC sp_executesql @SQL;

-- ---- Optionally reorganize dead-letter indexes ----
IF @IncludeDeadLetter = 1
BEGIN
    SET @SQL = N'ALTER INDEX ALL ON ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@DeadLetterTable) + N' REORGANIZE;';
    PRINT 'Executing: ' + @SQL;
    EXEC sp_executesql @SQL;

    SET @SQL = N'UPDATE STATISTICS ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@DeadLetterTable) + N';';
    PRINT 'Executing: ' + @SQL;
    EXEC sp_executesql @SQL;
END;

DECLARE @ElapsedMs INT = DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME());

-- ---- After snapshot ----
PRINT '';
PRINT '--- After Reorganize ---';
SELECT
    QUOTENAME(@SchemaName) + '.' + QUOTENAME(t.name) AS [Table],
    i.name AS [IndexName],
    CAST(ps.avg_fragmentation_in_percent AS DECIMAL(5,2)) AS [FragmentationPct],
    ps.ghost_record_count AS [GhostRecordCount],
    ps.page_count AS [PageCount]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes i ON t.object_id = i.object_id
CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), t.object_id, i.index_id, NULL, 'LIMITED') ps
WHERE s.name = @SchemaName
  AND (t.name = @OutboxTable OR (t.name = @DeadLetterTable AND @IncludeDeadLetter = 1))
  AND i.type > 0
ORDER BY t.name, i.index_id;

PRINT '';
PRINT 'Reorganize completed in ' + CAST(@ElapsedMs AS NVARCHAR(20)) + ' ms.';
```

- [ ] **Step 2: Commit**

```bash
git add src/Outbox.SqlServer/db_scripts/maintenance/reorganize_indexes.sql
git commit -m "feat: add index reorganize script for SQL Server ghost record cleanup

Online REORGANIZE on all Outbox indexes with before/after fragmentation
snapshots. Includes optional dead-letter table support and statistics update."
```

---

### Task 3: Rebuild Indexes Script

**Files:**
- Create: `src/Outbox.SqlServer/db_scripts/maintenance/rebuild_indexes.sql`

- [ ] **Step 1: Write `rebuild_indexes.sql`**

```sql
-- =============================================================================
-- rebuild_indexes.sql
-- Full index rebuild for severe fragmentation (>30%) or post-major-outage.
-- Enterprise/Azure SQL: ONLINE rebuild. Standard: OFFLINE (blocks writes).
-- Requires: ALTER permission on the Outbox table.
-- Schedule: monthly, or after major outage recovery.
-- =============================================================================

DECLARE @SchemaName SYSNAME = N'dbo';
DECLARE @TablePrefix NVARCHAR(128) = N'';
DECLARE @IncludeDeadLetter BIT = 0;  -- Set to 1 to also rebuild dead-letter indexes
DECLARE @MaxDop INT = 0;             -- 0 = system default

DECLARE @OutboxTable SYSNAME = @TablePrefix + N'Outbox';
DECLARE @DeadLetterTable SYSNAME = @TablePrefix + N'OutboxDeadLetter';

-- Edition detection for ONLINE support
DECLARE @EngineEdition INT = CAST(SERVERPROPERTY('EngineEdition') AS INT);
DECLARE @SupportsOnline BIT = CASE WHEN @EngineEdition IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @OnlineOption NVARCHAR(50) = CASE WHEN @SupportsOnline = 1 THEN N'ON' ELSE N'OFF' END;

PRINT '=== Outbox Index Rebuild ===';
PRINT 'Timestamp: ' + CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126) + ' UTC';
PRINT 'Online rebuild: ' + CASE WHEN @SupportsOnline = 1 THEN 'YES (Enterprise/Azure SQL)' ELSE 'NO (Standard edition — table will be locked during rebuild)' END;
PRINT 'MAXDOP: ' + CASE WHEN @MaxDop = 0 THEN 'system default' ELSE CAST(@MaxDop AS NVARCHAR(10)) END;

IF @SupportsOnline = 0
BEGIN
    PRINT '';
    PRINT '*** WARNING: OFFLINE REBUILD — all reads and writes on the Outbox table ***';
    PRINT '*** will be BLOCKED for the duration. Coordinate with publisher downtime. ***';
END;

PRINT '';

-- ---- Before snapshot ----
PRINT '--- Before Rebuild ---';
SELECT
    QUOTENAME(@SchemaName) + '.' + QUOTENAME(t.name) AS [Table],
    i.name AS [IndexName],
    CAST(ps.avg_fragmentation_in_percent AS DECIMAL(5,2)) AS [FragmentationPct],
    ps.ghost_record_count AS [GhostRecordCount],
    ps.page_count AS [PageCount]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes i ON t.object_id = i.object_id
CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), t.object_id, i.index_id, NULL, 'LIMITED') ps
WHERE s.name = @SchemaName
  AND (t.name = @OutboxTable OR (t.name = @DeadLetterTable AND @IncludeDeadLetter = 1))
  AND i.type > 0
ORDER BY t.name, i.index_id;

-- ---- Rebuild ----
DECLARE @SQL NVARCHAR(MAX);
DECLARE @StartTime DATETIME2 = SYSUTCDATETIME();

-- Rebuild all indexes on Outbox table
SET @SQL = N'ALTER INDEX ALL ON ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@OutboxTable)
    + N' REBUILD WITH (ONLINE = ' + @OnlineOption
    + N', MAXDOP = ' + CAST(@MaxDop AS NVARCHAR(10)) + N');';
PRINT 'Executing: ' + @SQL;
EXEC sp_executesql @SQL;

-- Update statistics
SET @SQL = N'UPDATE STATISTICS ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@OutboxTable) + N';';
PRINT 'Executing: ' + @SQL;
EXEC sp_executesql @SQL;

-- Optionally rebuild dead-letter indexes
IF @IncludeDeadLetter = 1
BEGIN
    SET @SQL = N'ALTER INDEX ALL ON ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@DeadLetterTable)
        + N' REBUILD WITH (ONLINE = ' + @OnlineOption
        + N', MAXDOP = ' + CAST(@MaxDop AS NVARCHAR(10)) + N');';
    PRINT 'Executing: ' + @SQL;
    EXEC sp_executesql @SQL;

    SET @SQL = N'UPDATE STATISTICS ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@DeadLetterTable) + N';';
    PRINT 'Executing: ' + @SQL;
    EXEC sp_executesql @SQL;
END;

DECLARE @ElapsedMs INT = DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME());

-- ---- After snapshot ----
PRINT '';
PRINT '--- After Rebuild ---';
SELECT
    QUOTENAME(@SchemaName) + '.' + QUOTENAME(t.name) AS [Table],
    i.name AS [IndexName],
    CAST(ps.avg_fragmentation_in_percent AS DECIMAL(5,2)) AS [FragmentationPct],
    ps.ghost_record_count AS [GhostRecordCount],
    ps.page_count AS [PageCount]
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes i ON t.object_id = i.object_id
CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), t.object_id, i.index_id, NULL, 'LIMITED') ps
WHERE s.name = @SchemaName
  AND (t.name = @OutboxTable OR (t.name = @DeadLetterTable AND @IncludeDeadLetter = 1))
  AND i.type > 0
ORDER BY t.name, i.index_id;

PRINT '';
PRINT 'Rebuild completed in ' + CAST(@ElapsedMs AS NVARCHAR(20)) + ' ms.';
```

- [ ] **Step 2: Commit**

```bash
git add src/Outbox.SqlServer/db_scripts/maintenance/rebuild_indexes.sql
git commit -m "feat: add index rebuild script with online/offline edition detection

Full REBUILD with automatic Enterprise/Azure SQL detection for ONLINE mode.
Falls back to OFFLINE with warning on Standard edition. Configurable MAXDOP."
```

---

### Task 4: Partition Switch Cleanup Script

**Files:**
- Create: `src/Outbox.SqlServer/db_scripts/maintenance/partition_switch_cleanup.sql`

**Reference:** The staging table must match the Outbox table schema exactly from `src/Outbox.SqlServer/db_scripts/install.sql` lines 15-33 (columns, computed column, PK) and lines 117-122 (IX_Outbox_Pending). The SWITCH operation requires identical schemas, constraints, and indexes.

- [ ] **Step 1: Write `partition_switch_cleanup.sql`**

```sql
-- =============================================================================
-- partition_switch_cleanup.sql
-- Emergency: instant physical cleanup of a fully drained Outbox table.
-- Uses ALTER TABLE ... SWITCH (metadata-only) + TRUNCATE (no ghost records).
-- Requires: ALTER on the table, CREATE TABLE in the schema.
-- Use ONLY when the Outbox table is completely empty and publishers are stopped.
-- =============================================================================

DECLARE @SchemaName SYSNAME = N'dbo';
DECLARE @TablePrefix NVARCHAR(128) = N'';

DECLARE @OutboxTable SYSNAME = @TablePrefix + N'Outbox';
DECLARE @StagingTable SYSNAME = @TablePrefix + N'Outbox_SwitchStaging';
DECLARE @OutboxFull NVARCHAR(256) = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@OutboxTable);
DECLARE @StagingFull NVARCHAR(256) = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@StagingTable);

PRINT '=== Outbox Partition Switch Cleanup ===';
PRINT 'Timestamp: ' + CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126) + ' UTC';
PRINT '';

-- ---- Pre-condition: table must be empty ----
DECLARE @RowCount BIGINT;
DECLARE @SQL NVARCHAR(MAX);

SET @SQL = N'SELECT @cnt = COUNT_BIG(*) FROM ' + @OutboxFull + N';';
EXEC sp_executesql @SQL, N'@cnt BIGINT OUTPUT', @cnt = @RowCount OUTPUT;

IF @RowCount > 0
BEGIN
    RAISERROR('ABORTED: %s contains %I64d rows. The table must be fully drained (0 rows) before running partition switch cleanup. Stop all publishers and wait for the outbox to drain.', 16, 1, @OutboxFull, @RowCount);
    RETURN;
END;

PRINT 'Pre-condition passed: table is empty.';

-- ---- Capture current identity value ----
DECLARE @CurrentIdentity BIGINT;
SET @SQL = N'SELECT @id = IDENT_CURRENT(''' + @SchemaName + N'.' + @OutboxTable + N''');';
EXEC sp_executesql @SQL, N'@id BIGINT OUTPUT', @id = @CurrentIdentity OUTPUT;
PRINT 'Current IDENTITY value: ' + CAST(@CurrentIdentity AS NVARCHAR(20));

-- ---- Capture space before ----
DECLARE @PagesBefore BIGINT;
SELECT @PagesBefore = SUM(a.total_pages)
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.partitions p ON t.object_id = p.object_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE s.name = @SchemaName AND t.name = @OutboxTable;

-- ---- Drop staging table if left over from a previous failed run ----
IF OBJECT_ID(@StagingFull) IS NOT NULL
BEGIN
    SET @SQL = N'DROP TABLE ' + @StagingFull + N';';
    EXEC sp_executesql @SQL;
    PRINT 'Dropped leftover staging table.';
END;

-- ---- Create staging table with identical schema ----
-- Must match install.sql exactly: columns, computed column, PK, indexes.
SET @SQL = N'
CREATE TABLE ' + @StagingFull + N'
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
    PayloadContentType NVARCHAR(100)         NOT NULL  DEFAULT ''application/json'',
    RetryCount         INT                   NOT NULL  DEFAULT 0,
    RowVersion         ROWVERSION            NOT NULL,
    PartitionId        AS (ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % 64) PERSISTED,

    CONSTRAINT PK_' + @StagingTable + N' PRIMARY KEY CLUSTERED (SequenceNumber)
);';
EXEC sp_executesql @SQL;
PRINT 'Created staging table with matching schema.';

-- Create matching nonclustered index (required for SWITCH)
SET @SQL = N'
CREATE NONCLUSTERED INDEX IX_' + @StagingTable + N'_Pending
ON ' + @StagingFull + N' (PartitionId, EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, RetryCount, CreatedAtUtc, RowVersion);';
EXEC sp_executesql @SQL;
PRINT 'Created matching nonclustered index on staging table.';

-- ---- SWITCH: instant metadata operation ----
SET @SQL = N'ALTER TABLE ' + @OutboxFull + N' SWITCH TO ' + @StagingFull + N';';
PRINT 'Executing: ' + @SQL;
EXEC sp_executesql @SQL;
PRINT 'Switch completed (metadata-only, instant).';

-- ---- TRUNCATE staging: instant, no ghost records ----
SET @SQL = N'TRUNCATE TABLE ' + @StagingFull + N';';
EXEC sp_executesql @SQL;
PRINT 'Staging table truncated (no ghost records generated).';

-- ---- Drop staging table ----
SET @SQL = N'DROP TABLE ' + @StagingFull + N';';
EXEC sp_executesql @SQL;
PRINT 'Staging table dropped.';

-- ---- Reseed identity ----
SET @SQL = N'DBCC CHECKIDENT(''' + @SchemaName + N'.' + @OutboxTable + N''', RESEED, ' + CAST(@CurrentIdentity AS NVARCHAR(20)) + N');';
EXEC sp_executesql @SQL;
PRINT 'IDENTITY reseeded to ' + CAST(@CurrentIdentity AS NVARCHAR(20)) + '.';

-- ---- Report space reclaimed ----
DECLARE @PagesAfter BIGINT;
SELECT @PagesAfter = SUM(a.total_pages)
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.partitions p ON t.object_id = p.object_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE s.name = @SchemaName AND t.name = @OutboxTable;

DECLARE @ReclaimedMB DECIMAL(10,2) = (@PagesBefore - @PagesAfter) * 8.0 / 1024;

PRINT '';
PRINT '=== Cleanup Complete ===';
PRINT 'Pages before: ' + CAST(@PagesBefore AS NVARCHAR(20));
PRINT 'Pages after:  ' + CAST(@PagesAfter AS NVARCHAR(20));
PRINT 'Space reclaimed: ' + CAST(@ReclaimedMB AS NVARCHAR(20)) + ' MB';
PRINT 'IDENTITY will continue from: ' + CAST(@CurrentIdentity + 1 AS NVARCHAR(20));
```

- [ ] **Step 2: Commit**

```bash
git add src/Outbox.SqlServer/db_scripts/maintenance/partition_switch_cleanup.sql
git commit -m "feat: add partition switch cleanup script for emergency table reset

Instant metadata-only SWITCH + TRUNCATE pattern for physically bloated
Outbox tables after major outages. Pre-condition: table must be empty."
```

---

### Task 5: Known Limitations Documentation

**Files:**
- Modify: `docs/known-limitations.md` (append after line 127)

- [ ] **Step 1: Add the ghost records limitation section**

Append to the end of `docs/known-limitations.md`:

```markdown

### SQL Server ghost records from continuous DELETE workload

**Affected:** SQL Server store only (PostgreSQL's VACUUM handles this natively)

The outbox pattern generates a continuous stream of DELETE operations as published messages are removed from the table. SQL Server does not immediately reclaim the physical space from deleted rows — instead it marks them as "ghost records" and relies on a background task (`ghost_cleanup`) to remove them asynchronously, typically within seconds.

Under normal load (< 1,000 msg/sec per publisher) the ghost cleanup task easily keeps pace and no operator action is needed. However, ghost records can accumulate faster than cleanup in two scenarios:

1. **Post-outage drain:** A prolonged broker outage (> 1 hour) causes millions of messages to accumulate in the outbox. When the broker recovers, the backlog drains rapidly — producing a burst of thousands of DELETEs per second that outpaces the ghost cleanup task.
2. **Sustained high throughput:** At 10K+ msg/sec across multiple publishers, continuous high-volume deletes can gradually outpace cleanup between cycles.

**Symptoms:** `IX_Outbox_Pending` bloats with ghost records and empty pages, causing `FetchBatchAsync` scans to slow down. Observable as increasing `outbox.poll.duration` p99 even when `outbox.messages.pending` is low.

**Mitigation:** Maintenance scripts are provided in `src/Outbox.SqlServer/db_scripts/maintenance/`. See the "SQL Server Index Maintenance" section in `docs/production-runbook.md` for scheduling guidance and procedures.
```

- [ ] **Step 2: Commit**

```bash
git add docs/known-limitations.md
git commit -m "docs: document SQL Server ghost records as known limitation

Explains the engine-level ghost record accumulation from continuous
DELETE workload and points to maintenance scripts and runbook."
```

---

### Task 6: Production Runbook — SQL Server Index Maintenance Section

**Files:**
- Modify: `docs/production-runbook.md` (insert new section before "Monitoring & Alerting Recommendations", after line 739 which ends the Emergency Procedures section)

- [ ] **Step 1: Add the SQL Server Index Maintenance section**

Insert the following after the `---` on line 739 (after "Emergency: Force Dead-Letter Cleanup") and before `## Monitoring & Alerting Recommendations`:

```markdown

## SQL Server Index Maintenance

The outbox's continuous DELETE workload generates SQL Server engine-level "ghost records" — rows marked for lazy background cleanup. Under normal load the built-in `ghost_cleanup` task keeps pace, but after prolonged broker outages or sustained high throughput, ghost records can accumulate and degrade poll performance.

Maintenance scripts are located in `src/Outbox.SqlServer/db_scripts/maintenance/`. All scripts accept `@SchemaName` (default `dbo`) and `@TablePrefix` (default empty) parameters to match your `SqlServerStoreOptions` configuration.

### When to Run Maintenance

| Scenario | Action | Urgency |
|----------|--------|---------|
| Routine preventive | `check_ghost_records.sql` → `reorganize_indexes.sql` | Weekly |
| Post broker outage (< 1 hour) | `check_ghost_records.sql` | After drain — usually no action needed |
| Post broker outage (> 1 hour) | `check_ghost_records.sql` → `reorganize_indexes.sql` | After drain completes |
| Post major outage (millions drained) | `check_ghost_records.sql` → `rebuild_indexes.sql` | After drain completes |
| Table bloated, fully drained | `partition_switch_cleanup.sql` | Emergency |
| `outbox.poll.duration` p99 rising, low pending count | `check_ghost_records.sql` to diagnose | Investigate immediately |

See also [FS-12: Outbox Table Growing Unbounded](#fs-12-outbox-table-growing-unbounded) — ghost record accumulation is one root cause of sustained table bloat.

### Monitoring Ghost Records

**Run the diagnostic script:**

```sql
-- Adjust @SchemaName and @TablePrefix to match your configuration
-- Requires VIEW DATABASE STATE permission
:r check_ghost_records.sql
```

**Interpreting results:**

| Recommendation | Meaning | Action |
|----------------|---------|--------|
| `OK` | Fragmentation < 10%, ghost records < 1,000 | No action needed |
| `REORGANIZE` | Fragmentation 10-30% or ghost records 1,000-10,000 | Run `reorganize_indexes.sql` during business hours |
| `REBUILD` | Fragmentation > 30% or ghost records > 10,000 | Run `rebuild_indexes.sql` (coordinate downtime on Standard edition) |
| `INVESTIGATE` | Table is small (< 1,000 pages) but ghost count is elevated | Ghost records may clear on their own — re-check in 10 minutes |

**Observable symptoms of ghost record accumulation:**

- `outbox.poll.duration` p99 increasing while `outbox.messages.pending` is low or stable
- `FetchBatchAsync` returns fewer rows than expected despite messages being available
- `sys.dm_db_index_physical_stats` shows `ghost_record_count` significantly higher than `record_count`

**Suggested alert rule:** If `outbox.poll.duration` p99 > 500ms AND `outbox.messages.pending` < 1,000 for > 5 minutes, run the ghost record check script.

### Routine Maintenance: REORGANIZE

**When:** Weekly preventive, or when the check script recommends `REORGANIZE`.

**Impact:** Online operation. No blocking. Safe to run during active publishing with minimal CPU overhead.

**Steps:**

1. Run `check_ghost_records.sql` and review the output
2. If any index shows `REORGANIZE` recommendation, run `reorganize_indexes.sql`:
   ```sql
   -- Set @IncludeDeadLetter = 1 if dead-letter table is also fragmented
   :r reorganize_indexes.sql
   ```
3. Verify the "After Reorganize" output shows reduced fragmentation and ghost count
4. Expected duration: seconds to low minutes depending on table size

**Scheduling:** Create a SQL Agent job or Azure Elastic Job that runs `check_ghost_records.sql` followed by `reorganize_indexes.sql` weekly during a low-traffic window.

### Deep Maintenance: REBUILD

**When:** Monthly preventive, when fragmentation exceeds 30%, or after a major outage where millions of rows were drained.

**Impact depends on SQL Server edition:**

| Edition | Behavior | Publisher Impact |
|---------|----------|-----------------|
| Enterprise | `ONLINE = ON` — brief schema lock at start and end | Minimal — publishers continue operating |
| Azure SQL Database | `ONLINE = ON` — same as Enterprise | Minimal |
| Azure SQL Managed Instance | `ONLINE = ON` — same as Enterprise | Minimal |
| Standard | `ONLINE = OFF` — **blocks all reads and writes** | **Publishers must be stopped or will timeout** |
| Express | `ONLINE = OFF` — same as Standard | **Publishers must be stopped** |

**Steps:**

1. Run `check_ghost_records.sql` and confirm `REBUILD` recommendation
2. **Standard/Express edition only:** Stop all publishers before proceeding
   ```bash
   kubectl scale deployment outbox-publisher --replicas=0
   ```
3. Run `rebuild_indexes.sql`:
   ```sql
   -- Adjust @MaxDop if needed (0 = system default)
   -- Set @IncludeDeadLetter = 1 if dead-letter table also needs rebuild
   :r rebuild_indexes.sql
   ```
4. Verify the "After Rebuild" output shows ~0% fragmentation and 0 ghost records
5. **Standard/Express edition only:** Restart publishers
   ```bash
   kubectl scale deployment outbox-publisher --replicas=<desired>
   ```
6. Expected duration: minutes for typical table sizes (< 1M rows), longer for very large tables

### Emergency: Partition Switch Cleanup

**When:** The Outbox table is physically bloated (hundreds of MB or GB of allocated space) after draining millions of rows following a major outage. The table is empty but the physical pages are still allocated with ghost records.

**Pre-conditions:**
- Outbox table MUST contain 0 rows (fully drained)
- All publishers SHOULD be stopped (no concurrent inserts during the switch)

**Impact:** Instant metadata operation — no data movement, no ghost records, no transaction log bloat. But requires exclusive access to the table.

**Steps:**

1. Stop all publishers:
   ```bash
   kubectl scale deployment outbox-publisher --replicas=0
   ```
2. Verify the outbox table is empty:
   ```sql
   SELECT COUNT(*) FROM dbo.Outbox;
   -- Must return 0
   ```
3. Run `partition_switch_cleanup.sql`:
   ```sql
   :r partition_switch_cleanup.sql
   ```
4. Verify the output shows space reclaimed and IDENTITY reseeded
5. Restart publishers:
   ```bash
   kubectl scale deployment outbox-publisher --replicas=<desired>
   ```

**If the script aborts:** It will print an error message indicating rows still exist. Wait for the outbox to fully drain, or investigate why messages are stuck (see [FS-12](#fs-12-outbox-table-growing-unbounded)).

### Permissions Required

| Script | Permission | Scope |
|--------|-----------|-------|
| `check_ghost_records.sql` | `VIEW DATABASE STATE` | Database-level |
| `reorganize_indexes.sql` | `ALTER` | On the Outbox table |
| `rebuild_indexes.sql` | `ALTER` | On the Outbox table |
| `partition_switch_cleanup.sql` | `ALTER`, `CREATE TABLE` | On the Outbox table + schema |

**Minimum-privilege setup:**

```sql
-- Create a dedicated role for outbox maintenance
CREATE ROLE outbox_maintenance;

-- Grant required permissions
GRANT VIEW DATABASE STATE TO outbox_maintenance;
GRANT ALTER ON dbo.Outbox TO outbox_maintenance;
GRANT ALTER ON dbo.OutboxDeadLetter TO outbox_maintenance;
GRANT CREATE TABLE TO outbox_maintenance;

-- Add the maintenance account to the role
ALTER ROLE outbox_maintenance ADD MEMBER [your_maintenance_account];
```
```

- [ ] **Step 2: Commit**

```bash
git add docs/production-runbook.md
git commit -m "docs: add SQL Server index maintenance runbook section

Covers ghost record monitoring, REORGANIZE, REBUILD, and emergency
partition switch procedures with edition-specific guidance, scheduling
recommendations, and minimum-privilege permission setup."
```

---

### Task 7: Final Verification

- [ ] **Step 1: Verify all files exist**

```bash
ls -la src/Outbox.SqlServer/db_scripts/maintenance/
```

Expected output: 4 `.sql` files:
- `check_ghost_records.sql`
- `reorganize_indexes.sql`
- `rebuild_indexes.sql`
- `partition_switch_cleanup.sql`

- [ ] **Step 2: Verify docs were updated**

```bash
grep -n "ghost record" docs/known-limitations.md
grep -n "SQL Server Index Maintenance" docs/production-runbook.md
```

Expected: matches in both files confirming the new sections exist.

- [ ] **Step 3: Verify the solution builds**

```bash
dotnet build src/Outbox.slnx
```

Expected: build succeeds (no code changes, only SQL scripts and docs).
