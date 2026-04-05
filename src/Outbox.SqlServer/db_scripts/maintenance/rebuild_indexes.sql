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
