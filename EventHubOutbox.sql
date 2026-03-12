-- =============================================================================
-- EventHub Outbox Pattern — SQL Server
-- =============================================================================
-- Sections:
--   1. Schema: dbo.Outbox, dbo.OutboxDeadLetter, dbo.OutboxProducers,
--              dbo.OutboxPartitions
--   2. Table-Valued Parameter type: dbo.SequenceNumberList
--   3. Indexes
--   4. Publisher queries: unified poll, delete, release, dead-letter
--   5. Producer registration and heartbeat
--   6. Partition registration and rebalance
--   7. Monitoring queries
--   8. Cleanup and maintenance scripts
-- =============================================================================

-- =============================================================================
-- SECTION 1: SCHEMA
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1a. Outbox — primary event buffer
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.Outbox
(
    SequenceNumber  BIGINT IDENTITY(1,1)  NOT NULL,
    TopicName       NVARCHAR(256)         NOT NULL,
    PartitionKey    NVARCHAR(256)         NOT NULL,
    EventType       NVARCHAR(256)         NOT NULL,
    Headers         NVARCHAR(4000)        NULL,
    Payload         NVARCHAR(4000)        NOT NULL,
    CreatedAtUtc    DATETIME2(3)          NOT NULL  DEFAULT SYSUTCDATETIME(),
    EventDateTimeUtc DATETIME2(3)         NOT NULL,
    EventOrdinal    SMALLINT              NOT NULL  DEFAULT 0,
    LeasedUntilUtc  DATETIME2(3)          NULL,
    LeaseOwner      NVARCHAR(128)         NULL,
    RetryCount      INT                   NOT NULL  DEFAULT 0,

    CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (SequenceNumber)
);
GO

-- ---------------------------------------------------------------------------
-- 1b. OutboxDeadLetter — rows that exceeded the retry threshold
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.OutboxDeadLetter
(
    DeadLetterSeq    BIGINT IDENTITY(1,1)  NOT NULL,
    SequenceNumber   BIGINT                NOT NULL,   -- original SequenceNumber
    TopicName        NVARCHAR(256)         NOT NULL,
    PartitionKey     NVARCHAR(256)         NOT NULL,
    EventType        NVARCHAR(256)         NOT NULL,
    Headers          NVARCHAR(4000)        NULL,
    Payload          NVARCHAR(4000)        NOT NULL,
    CreatedAtUtc     DATETIME2(3)          NOT NULL,
    RetryCount       INT                   NOT NULL,
    EventDateTimeUtc  DATETIME2(3)         NOT NULL,
    EventOrdinal      SMALLINT            NOT NULL  DEFAULT 0,
    DeadLetteredAtUtc DATETIME2(3)         NOT NULL  DEFAULT SYSUTCDATETIME(),
    LastError        NVARCHAR(2000)        NULL,

    CONSTRAINT PK_OutboxDeadLetter PRIMARY KEY CLUSTERED (DeadLetterSeq)
);
GO

-- ---------------------------------------------------------------------------
-- 1c. OutboxProducers — heartbeat registry for active publisher instances
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.OutboxProducers
(
    ProducerId        NVARCHAR(128)  NOT NULL,
    RegisteredAtUtc   DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
    LastHeartbeatUtc  DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
    HostName          NVARCHAR(256)  NULL,

    CONSTRAINT PK_OutboxProducers PRIMARY KEY CLUSTERED (ProducerId)
);
GO

-- ---------------------------------------------------------------------------
-- 1d. OutboxPartitions — partition affinity assignment map
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.OutboxPartitions
(
    PartitionId        INT            NOT NULL,
    OwnerProducerId    NVARCHAR(128)  NULL,   -- FK to OutboxProducers; NULL = unowned
    OwnedSinceUtc      DATETIME2(3)   NULL,
    GraceExpiresUtc    DATETIME2(3)   NULL,   -- handover grace window; stealable after this

    CONSTRAINT PK_OutboxPartitions PRIMARY KEY CLUSTERED (PartitionId)
);
GO

-- =============================================================================
-- SECTION 2: TABLE-VALUED PARAMETER TYPE
-- =============================================================================

-- dbo.SequenceNumberList replaces OPENJSON(@PublishedIds) in the delete query.
-- Benefits over OPENJSON:
--   - Proper cardinality estimates (OPENJSON always estimates 50 rows)
--   - No JSON serialisation/parse overhead on the hot delete path
--   - Better plan caching across different batch sizes
CREATE TYPE dbo.SequenceNumberList AS TABLE
(
    SequenceNumber BIGINT NOT NULL PRIMARY KEY
);
GO

-- =============================================================================
-- SECTION 3: INDEXES
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Unified poll (fresh rows arm): "give me unleased rows in causal order"
-- Leading on (EventDateTimeUtc, EventOrdinal) to match the unified poll's
-- ORDER BY. Filtered on LeasedUntilUtc IS NULL so the index only contains
-- fresh rows and stays small at steady state. INCLUDE covers all columns
-- needed by the publisher so no key lookups are required. SQL Server can
-- use an index union of this index and IX_Outbox_LeaseExpiry to serve the
-- unified poll's (LeasedUntilUtc IS NULL OR LeasedUntilUtc < NOW) predicate.
-- ---------------------------------------------------------------------------
CREATE NONCLUSTERED INDEX IX_Outbox_Unleased
ON dbo.Outbox (EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
WHERE LeasedUntilUtc IS NULL;
GO

-- ---------------------------------------------------------------------------
-- Unified poll (expired rows arm): "give me rows whose lease has expired"
-- Leading column LeasedUntilUtc allows an efficient range seek for
-- LeasedUntilUtc < SYSUTCDATETIME() without scanning active leases.
-- ---------------------------------------------------------------------------
CREATE NONCLUSTERED INDEX IX_Outbox_LeaseExpiry
ON dbo.Outbox (LeasedUntilUtc, EventDateTimeUtc, EventOrdinal)
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
WHERE LeasedUntilUtc IS NOT NULL;
GO

-- =============================================================================
-- SECTION 4: PUBLISHER QUERIES
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 4a. Unified poll — lease both fresh and expired-lease rows in one pass.
--
-- This single query replaces the former separate primary (unleased) and
-- recovery (expired-lease) polls. By using ORDER BY EventDateTimeUtc,
-- EventOrdinal across both row sets, it guarantees that events are published
-- in application-controlled causal order, regardless of the database
-- IDENTITY insertion order. This preserves per-PartitionKey ordering even
-- during crash recovery and when using ORMs that do not guarantee INSERT
-- order within a single SaveChanges() call.
--
-- RetryCount is conditionally incremented only for rows that were previously
-- leased (LeasedUntilUtc IS NOT NULL), which identifies them as recovery
-- rows. Fresh rows (LeasedUntilUtc IS NULL) keep RetryCount = 0.
--
-- Parameters:
--   @BatchSize             INT            — rows to lease per call
--   @LeaseDurationSeconds  INT            — seconds until lease expires
--   @PublisherId           NVARCHAR(128)  — identity of this publisher instance
--   @TotalPartitions       INT            — total number of EventHub partitions
--   @MaxRetryCount         INT            — rows at or above this count are NOT
--                                          re-leased; they are dead-lettered
--                                          inline or by the sweep (4d)
--
-- Notes:
--   ROWLOCK   — prevent lock escalation so READPAST works correctly
--   READPAST  — skip rows locked by other publishers (zero blocking)
--   OUTPUT    — return all columns needed for EventHub publish in one round-trip
-- ---------------------------------------------------------------------------
/*
DECLARE @BatchSize            INT           = 100;
DECLARE @LeaseDurationSeconds INT           = 45;
DECLARE @PublisherId          NVARCHAR(128) = N'publisher-01';
DECLARE @TotalPartitions      INT           = 8;
DECLARE @MaxRetryCount        INT           = 5;

WITH Batch AS
(
    SELECT TOP (@BatchSize)
        o.SequenceNumber,
        o.LeasedUntilUtc,
        o.LeaseOwner,
        o.RetryCount
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    INNER JOIN dbo.OutboxPartitions op
        ON  op.OwnerProducerId = @PublisherId
        AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
        AND (ABS(CHECKSUM(o.PartitionKey)) % @TotalPartitions) = op.PartitionId
    WHERE (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME())
      AND o.RetryCount < @MaxRetryCount
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
       inserted.EventDateTimeUtc,
       inserted.EventOrdinal,
       inserted.RetryCount;
*/

-- ---------------------------------------------------------------------------
-- 4b. Delete after successful EventHub send — TVP version.
--
-- Uses dbo.SequenceNumberList TVP instead of OPENJSON for correct cardinality
-- estimates and better query plan stability.
--
-- The LeaseOwner guard prevents a slow/zombie publisher from deleting rows that
-- have been re-leased to another instance after the lease expired.
--
-- Parameters:
--   @PublishedIds  dbo.SequenceNumberList  — TVP of SequenceNumbers to delete
--   @PublisherId   NVARCHAR(128)           — must match LeaseOwner on each row
-- ---------------------------------------------------------------------------
/*
DECLARE @PublishedIds dbo.SequenceNumberList;
-- INSERT INTO @PublishedIds (SequenceNumber) VALUES (1), (2), (3), ...;

DECLARE @PublisherId NVARCHAR(128) = N'publisher-01';

DELETE o
FROM   dbo.Outbox o
INNER JOIN @PublishedIds p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;
*/

-- ---------------------------------------------------------------------------
-- 4c. Release leased rows — return rows to the unleased pool without
--     incrementing RetryCount. Used when the circuit breaker is open:
--     rows were already leased by the unified poll but cannot be sent.
--
-- Parameters:
--   @Ids          dbo.SequenceNumberList  — TVP of SequenceNumbers to release
--   @PublisherId  NVARCHAR(128)           — must match LeaseOwner on each row
-- ---------------------------------------------------------------------------
/*
DECLARE @Ids dbo.SequenceNumberList;
-- INSERT INTO @Ids (SequenceNumber) VALUES (1), (2), (3), ...;

DECLARE @PublisherId NVARCHAR(128) = N'publisher-01';

UPDATE o
SET    o.LeasedUntilUtc = NULL,
       o.LeaseOwner     = NULL
FROM   dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;
*/

-- ---------------------------------------------------------------------------
-- 4d. Dead-letter sweep — move poison messages to dbo.OutboxDeadLetter.
--
-- Uses DELETE...OUTPUT INTO instead of separate INSERT + DELETE so that the
-- set of rows removed from dbo.Outbox is guaranteed to be exactly the set
-- inserted into dbo.OutboxDeadLetter. The previous INSERT + DELETE pattern
-- was not atomic: INSERT used READPAST (skipping locked rows) but DELETE
-- did not, so they could operate on different row sets — risking data loss
-- (row deleted without being dead-lettered) or duplicates under concurrency.
--
-- Runs periodically (every @DeadLetterSweepIntervalSeconds, default 60s).
-- Also called directly by the publisher when it detects RetryCount >= @MaxRetryCount
-- during its own processing loop (belt-and-suspenders against the sweep).
--
-- Parameters:
--   @MaxRetryCount  INT             — rows at or above this count are dead-lettered
--   @LastError      NVARCHAR(2000)  — last exception message; NULL when called by sweep
-- ---------------------------------------------------------------------------
/*
DECLARE @MaxRetryCount INT           = 5;
DECLARE @LastError     NVARCHAR(2000) = NULL;

-- DELETE...OUTPUT INTO is a single atomic statement; no explicit transaction needed.
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.CreatedAtUtc, deleted.RetryCount,
       deleted.EventDateTimeUtc, deleted.EventOrdinal,
       SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, CreatedAtUtc, RetryCount,
     EventDateTimeUtc, EventOrdinal,
     DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
WHERE o.RetryCount >= @MaxRetryCount
  AND (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME());
*/

-- =============================================================================
-- SECTION 5: PRODUCER REGISTRATION AND HEARTBEAT
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 5a. Register producer on startup (idempotent upsert via MERGE).
--     Handles concurrent registration: the second MERGE for the same ProducerId
--     updates the heartbeat rather than inserting a duplicate, avoiding primary
--     key violation in the producer registration race.
--
-- Parameters:
--   @ProducerId  NVARCHAR(128)
--   @HostName    NVARCHAR(256)
-- ---------------------------------------------------------------------------
/*
DECLARE @ProducerId NVARCHAR(128) = N'publisher-01';
DECLARE @HostName   NVARCHAR(256) = N'app-server-01';

MERGE dbo.OutboxProducers WITH (HOLDLOCK) AS target
USING (SELECT @ProducerId AS ProducerId, @HostName AS HostName) AS source
    ON target.ProducerId = source.ProducerId
WHEN MATCHED THEN
    UPDATE SET LastHeartbeatUtc = SYSUTCDATETIME(),
               HostName         = source.HostName
WHEN NOT MATCHED THEN
    INSERT (ProducerId, RegisteredAtUtc, LastHeartbeatUtc, HostName)
    VALUES (source.ProducerId, SYSUTCDATETIME(), SYSUTCDATETIME(), source.HostName);
*/

-- ---------------------------------------------------------------------------
-- 5b. Heartbeat renewal — called every @HeartbeatIntervalSeconds (default 10s).
--
-- Parameters:
--   @ProducerId  NVARCHAR(128)
-- ---------------------------------------------------------------------------
/*
DECLARE @ProducerId NVARCHAR(128) = N'publisher-01';

UPDATE dbo.OutboxProducers
SET    LastHeartbeatUtc = SYSUTCDATETIME()
WHERE  ProducerId = @ProducerId;

-- If a rebalance marked our partitions with a grace window while we were
-- briefly unresponsive, clear it now that we are heartbeating again.
UPDATE dbo.OutboxPartitions
SET    GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId
  AND  GraceExpiresUtc IS NOT NULL;
*/

-- ---------------------------------------------------------------------------
-- 5c. Graceful unregistration — called on orderly shutdown.
--     Releases all owned partitions immediately (no grace period needed because
--     the outgoing publisher is done and has no in-flight leases).
--
-- Parameters:
--   @ProducerId  NVARCHAR(128)
-- ---------------------------------------------------------------------------
/*
DECLARE @ProducerId NVARCHAR(128) = N'publisher-01';

BEGIN TRANSACTION;

    -- Release owned partitions immediately (no grace period on graceful shutdown)
    UPDATE dbo.OutboxPartitions
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId = @ProducerId;

    -- Remove producer record
    DELETE FROM dbo.OutboxProducers
    WHERE  ProducerId = @ProducerId;

COMMIT TRANSACTION;
*/

-- =============================================================================
-- SECTION 6: PARTITION REGISTRATION AND REBALANCE
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 6a. Initialise partition table — run once at deployment time.
--     @PartitionCount must match the EventHub partition count for the namespace.
--
-- Parameters:
--   @PartitionCount  INT  — number of EventHub partitions (e.g., 8, 16, 32)
-- ---------------------------------------------------------------------------
/*
DECLARE @PartitionCount INT = 8;
DECLARE @i              INT = 0;

WHILE @i < @PartitionCount
BEGIN
    INSERT INTO dbo.OutboxPartitions (PartitionId, OwnerProducerId, OwnedSinceUtc, GraceExpiresUtc)
    SELECT @i, NULL, NULL, NULL
    WHERE NOT EXISTS (SELECT 1 FROM dbo.OutboxPartitions WHERE PartitionId = @i);

    SET @i = @i + 1;
END;
*/

-- ---------------------------------------------------------------------------
-- 6b. Rebalance — claim + release in a single transaction.
--
-- Runs claim and release atomically so concurrent publishers cannot observe
-- an inconsistent partition assignment mid-rebalance.
--
-- Steps:
--   1. Mark stale producers' partitions as entering grace period.
--   2. Claim unowned / grace-expired partitions up to fair share.
--   3. Re-check ownership count and release excess above fair share.
--
-- Parameters:
--   @ProducerId                  NVARCHAR(128)
--   @HeartbeatTimeoutSeconds     INT
--   @PartitionGracePeriodSeconds INT
-- ---------------------------------------------------------------------------
/*
DECLARE @ProducerId                  NVARCHAR(128) = N'publisher-01';
DECLARE @HeartbeatTimeoutSeconds     INT           = 30;
DECLARE @PartitionGracePeriodSeconds INT           = 60;

BEGIN TRANSACTION;

DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

IF @ToAcquire > 0
BEGIN
    -- Mark stale partitions as entering grace period before claiming
    UPDATE dbo.OutboxPartitions
    SET    GraceExpiresUtc = DATEADD(SECOND, @PartitionGracePeriodSeconds, SYSUTCDATETIME())
    WHERE  OwnerProducerId <> @ProducerId
      AND  OwnerProducerId IS NOT NULL
      AND  GraceExpiresUtc IS NULL
      AND  OwnerProducerId NOT IN
           (
               SELECT ProducerId
               FROM   dbo.OutboxProducers
               WHERE  LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
           );

    -- Claim unowned or grace-expired partitions up to fair share
    UPDATE TOP (@ToAcquire) dbo.OutboxPartitions WITH (UPDLOCK)
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    WHERE  (OwnerProducerId IS NULL
            OR GraceExpiresUtc < SYSUTCDATETIME());
END;

-- Recalculate after claims to check if we need to release excess
SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

IF @CurrentlyOwned > @FairShare
BEGIN
    DECLARE @ToRelease INT = @CurrentlyOwned - @FairShare;

    UPDATE TOP (@ToRelease) dbo.OutboxPartitions
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId = @ProducerId;
END;

COMMIT TRANSACTION;
*/

-- ---------------------------------------------------------------------------
-- 6c. Orphan sweep — pick up rows belonging to unowned partitions.
--     Runs every @OrphanSweepIntervalSeconds (default 60s).
--     Any publisher can claim an unowned partition and process its rows.
-- ---------------------------------------------------------------------------
/*
DECLARE @ProducerId              NVARCHAR(128) = N'publisher-01';
DECLARE @HeartbeatTimeoutSeconds INT           = 30;

DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

-- Only claim partitions that are truly unowned (NULL owner)
IF @ToAcquire > 0
BEGIN
    UPDATE TOP (@ToAcquire) dbo.OutboxPartitions WITH (UPDLOCK)
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId IS NULL;
END;
*/

-- =============================================================================
-- SECTION 7: MONITORING QUERIES
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 7a. Outbox depth and lease distribution
--     Uses DATEDIFF_BIG(MILLISECOND, ...) for sub-second precision.
--     The original DATEDIFF(SECOND, ...) truncates at whole-second boundaries.
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*)                                                                        AS TotalRows,
    SUM(CASE WHEN LeasedUntilUtc IS NULL THEN 1 ELSE 0 END)                        AS Unleased,
    SUM(CASE WHEN LeasedUntilUtc >= SYSUTCDATETIME() THEN 1 ELSE 0 END)            AS ActivelyLeased,
    SUM(CASE WHEN LeasedUntilUtc IS NOT NULL
              AND LeasedUntilUtc < SYSUTCDATETIME() THEN 1 ELSE 0 END)             AS ExpiredLeases,
    MIN(CreatedAtUtc)                                                               AS OldestMessage,
    DATEDIFF_BIG(MILLISECOND, MIN(CreatedAtUtc), SYSUTCDATETIME())                 AS MaxLatencyMs,
    MAX(RetryCount)                                                                 AS MaxRetryCount
FROM dbo.Outbox;
GO

-- ---------------------------------------------------------------------------
-- 7b. Per-topic depth
-- ---------------------------------------------------------------------------
SELECT   TopicName, COUNT(*) AS Pending
FROM     dbo.Outbox
GROUP BY TopicName
ORDER BY Pending DESC;
GO

-- ---------------------------------------------------------------------------
-- 7c. Active producers and heartbeat ages
-- ---------------------------------------------------------------------------
SELECT
    ProducerId,
    HostName,
    RegisteredAtUtc,
    LastHeartbeatUtc,
    DATEDIFF_BIG(MILLISECOND, LastHeartbeatUtc, SYSUTCDATETIME()) AS HeartbeatAgeMs
FROM dbo.OutboxProducers
ORDER BY LastHeartbeatUtc DESC;
GO

-- ---------------------------------------------------------------------------
-- 7d. Partition ownership and status
-- ---------------------------------------------------------------------------
SELECT
    p.PartitionId,
    p.OwnerProducerId,
    pr.HostName,
    p.OwnedSinceUtc,
    p.GraceExpiresUtc,
    CASE
        WHEN p.OwnerProducerId IS NULL THEN 'UNOWNED'
        WHEN pr.ProducerId IS NULL THEN 'ORPHANED'
        WHEN p.GraceExpiresUtc IS NOT NULL
             AND p.GraceExpiresUtc > SYSUTCDATETIME() THEN 'IN_GRACE'
        ELSE 'OWNED'
    END AS Status
FROM dbo.OutboxPartitions p
LEFT JOIN dbo.OutboxProducers pr
    ON pr.ProducerId = p.OwnerProducerId
ORDER BY p.PartitionId;
GO

-- ---------------------------------------------------------------------------
-- 7e. Dead-letter queue depth
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*)               AS DeadLetterCount,
    MIN(DeadLetteredAtUtc) AS OldestDeadLetter,
    MAX(RetryCount)        AS MaxRetries
FROM dbo.OutboxDeadLetter;
GO

-- ---------------------------------------------------------------------------
-- 7f. SLA breach — rows older than 5 minutes
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*) AS SlaBreachCount,
    MIN(CreatedAtUtc) AS OldestSlaBreachMessage
FROM dbo.Outbox
WHERE CreatedAtUtc < DATEADD(MINUTE, -5, SYSUTCDATETIME());
GO

-- ---------------------------------------------------------------------------
-- 7g. Index fragmentation check
-- ---------------------------------------------------------------------------
SELECT
    i.name                          AS IndexName,
    s.avg_fragmentation_in_percent  AS FragmentationPct,
    s.page_count                    AS PageCount,
    s.record_count                  AS RecordCount
FROM sys.dm_db_index_physical_stats(
    DB_ID(), OBJECT_ID('dbo.Outbox'), NULL, NULL, 'LIMITED') s
INNER JOIN sys.indexes i
    ON i.object_id = s.object_id
   AND i.index_id  = s.index_id
ORDER BY s.avg_fragmentation_in_percent DESC;
GO

-- ---------------------------------------------------------------------------
-- 7h. Per-producer lease activity
-- ---------------------------------------------------------------------------
SELECT
    LeaseOwner,
    COUNT(*)                                                    AS LeasedRows,
    SUM(CASE WHEN LeasedUntilUtc < SYSUTCDATETIME() THEN 1
             ELSE 0 END)                                        AS ExpiredRows,
    MIN(LeasedUntilUtc)                                         AS EarliestExpiry,
    MAX(LeasedUntilUtc)                                         AS LatestExpiry
FROM dbo.Outbox
WHERE LeaseOwner IS NOT NULL
GROUP BY LeaseOwner
ORDER BY LeasedRows DESC;
GO

-- =============================================================================
-- SECTION 8: CLEANUP AND MAINTENANCE SCRIPTS
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 8a. Rebuild all indexes on Outbox — near-instant when table is small
-- ---------------------------------------------------------------------------
/*
ALTER INDEX ALL ON dbo.Outbox REBUILD;
ALTER INDEX ALL ON dbo.OutboxDeadLetter REBUILD;
*/

-- ---------------------------------------------------------------------------
-- 8b. Archive dead-letter rows older than 30 days
-- ---------------------------------------------------------------------------
/*
DELETE FROM dbo.OutboxDeadLetter
WHERE DeadLetteredAtUtc < DATEADD(DAY, -30, SYSUTCDATETIME());
*/

-- ---------------------------------------------------------------------------
-- 8c. Force rebalance — clears the producer registry so all publishers
--     re-register and re-claim partitions on their next heartbeat cycle.
--     Use with caution: all partition ownership is reset.
-- ---------------------------------------------------------------------------
/*
BEGIN TRANSACTION;
    UPDATE dbo.OutboxPartitions
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL;

    DELETE FROM dbo.OutboxProducers;
COMMIT TRANSACTION;
*/

-- ---------------------------------------------------------------------------
-- 8d. Replay dead-letter rows — move selected rows back to dbo.Outbox
--     for reprocessing. Use SequenceNumber range or specific IDs.
-- ---------------------------------------------------------------------------
/*
DECLARE @ReplayIds dbo.SequenceNumberList;
-- INSERT INTO @ReplayIds VALUES (1001), (1002);

BEGIN TRANSACTION;

    INSERT INTO dbo.Outbox
        (TopicName, PartitionKey, EventType, Headers, Payload, CreatedAtUtc,
         EventDateTimeUtc, EventOrdinal, LeasedUntilUtc, LeaseOwner, RetryCount)
    SELECT
        TopicName, PartitionKey, EventType, Headers, Payload, CreatedAtUtc,
        EventDateTimeUtc, EventOrdinal, NULL, NULL, 0   -- reset retry count for clean replay
    FROM dbo.OutboxDeadLetter dl
    INNER JOIN @ReplayIds r ON dl.SequenceNumber = r.SequenceNumber;

    DELETE dl
    FROM dbo.OutboxDeadLetter dl
    INNER JOIN @ReplayIds r ON dl.SequenceNumber = r.SequenceNumber;

COMMIT TRANSACTION;
*/

-- ---------------------------------------------------------------------------
-- 8e. Emergency cleanup — delete all rows for a specific topic
--     (e.g., after a bad deployment that flooded the outbox with bad events)
-- ---------------------------------------------------------------------------
/*
DECLARE @TopicName NVARCHAR(256) = N'orders';

DELETE FROM dbo.Outbox
WHERE TopicName = @TopicName;
*/
