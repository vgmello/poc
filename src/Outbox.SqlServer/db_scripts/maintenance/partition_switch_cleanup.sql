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
