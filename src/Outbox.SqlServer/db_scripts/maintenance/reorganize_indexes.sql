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
