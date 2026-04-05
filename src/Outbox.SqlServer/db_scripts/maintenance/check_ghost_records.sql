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
