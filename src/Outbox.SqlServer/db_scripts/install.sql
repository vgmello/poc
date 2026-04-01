-- =============================================================================
-- Outbox SQL Server Schema — install script
-- Run once at deployment time. All statements are idempotent (IF NOT EXISTS).
-- =============================================================================

-- =============================================================================
-- SECTION 1: TABLES
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1a. Outbox — primary event buffer
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.Outbox') AND type = N'U')
BEGIN
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

        CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (SequenceNumber)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- 1b. OutboxDeadLetter — rows that exceeded the retry threshold
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.OutboxDeadLetter') AND type = N'U')
BEGIN
    CREATE TABLE dbo.OutboxDeadLetter
    (
        DeadLetterSeq     BIGINT IDENTITY(1,1)  NOT NULL,
        SequenceNumber    BIGINT                NOT NULL,
        TopicName         NVARCHAR(256)         NOT NULL,
        PartitionKey      NVARCHAR(256)         NOT NULL,
        EventType         NVARCHAR(256)         NOT NULL,
        Headers           NVARCHAR(MAX)         NULL,
        Payload           VARBINARY(MAX)        NOT NULL,
        CreatedAtUtc      DATETIME2(3)          NOT NULL,
        RetryCount        INT                   NOT NULL,
        EventDateTimeUtc  DATETIME2(3)          NOT NULL,
        EventOrdinal      INT                   NOT NULL  DEFAULT 0,
        PayloadContentType NVARCHAR(100)        NOT NULL  DEFAULT 'application/json',
        DeadLetteredAtUtc DATETIME2(3)          NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastError         NVARCHAR(2000)        NULL,

        CONSTRAINT PK_OutboxDeadLetter PRIMARY KEY CLUSTERED (DeadLetterSeq)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- 1c. OutboxPublishers — heartbeat registry for active publisher instances
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.OutboxPublishers') AND type = N'U')
BEGIN
    CREATE TABLE dbo.OutboxPublishers
    (
        OutboxTableName    NVARCHAR(256)  NOT NULL,
        PublisherId        NVARCHAR(128)  NOT NULL,
        RegisteredAtUtc   DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastHeartbeatUtc  DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
        HostName          NVARCHAR(256)  NULL,

        CONSTRAINT PK_OutboxPublishers PRIMARY KEY CLUSTERED (OutboxTableName, PublisherId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- 1d. OutboxPartitions — partition affinity assignment map
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.OutboxPartitions') AND type = N'U')
BEGIN
    CREATE TABLE dbo.OutboxPartitions
    (
        OutboxTableName     NVARCHAR(256)  NOT NULL,
        PartitionId        INT            NOT NULL,
        OwnerPublisherId    NVARCHAR(128)  NULL,
        OwnedSinceUtc      DATETIME2(3)   NULL,
        GraceExpiresUtc    DATETIME2(3)   NULL,

        CONSTRAINT PK_OutboxPartitions PRIMARY KEY CLUSTERED (OutboxTableName, PartitionId)
    );
END;
GO

-- =============================================================================
-- SECTION 2: TABLE-VALUED PARAMETER TYPE
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = N'SequenceNumberList' AND schema_id = SCHEMA_ID(N'dbo'))
BEGIN
    CREATE TYPE dbo.SequenceNumberList AS TABLE
    (
        SequenceNumber BIGINT NOT NULL PRIMARY KEY
    );
END;
GO

-- =============================================================================
-- SECTION 3: INDEXES
-- =============================================================================

-- Pending rows in causal order for polling
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Outbox') AND name = N'IX_Outbox_Pending')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Outbox_Pending
    ON dbo.Outbox (EventDateTimeUtc, EventOrdinal)
    INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, RetryCount, CreatedAtUtc);
END;
GO

-- Dead-letter lookup by original sequence number (used by replay and purge)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OutboxDeadLetter') AND name = N'IX_OutboxDeadLetter_SequenceNumber')
BEGIN
    CREATE NONCLUSTERED INDEX IX_OutboxDeadLetter_SequenceNumber
    ON dbo.OutboxDeadLetter (SequenceNumber);
END;
GO

-- =============================================================================
-- SECTION 4: DIAGNOSTIC VIEWS
-- =============================================================================

CREATE OR ALTER VIEW dbo.vw_Outbox AS
SELECT SequenceNumber, TopicName, PartitionKey, EventType,
       PayloadContentType,
       Headers,
       CASE WHEN PayloadContentType IN ('application/json', 'text/plain')
            THEN CAST(Payload AS VARCHAR(MAX))
       END AS PayloadText,
       RetryCount, CreatedAtUtc, EventDateTimeUtc
FROM dbo.Outbox;
GO

CREATE OR ALTER VIEW dbo.vw_OutboxDeadLetter AS
SELECT DeadLetterSeq, SequenceNumber, TopicName, PartitionKey, EventType,
       PayloadContentType,
       Headers,
       CASE WHEN PayloadContentType IN ('application/json', 'text/plain')
            THEN CAST(Payload AS VARCHAR(MAX))
       END AS PayloadText,
       RetryCount, CreatedAtUtc, EventDateTimeUtc,
       DeadLetteredAtUtc, LastError
FROM dbo.OutboxDeadLetter;
GO

-- =============================================================================
-- SECTION 5: SEED DEFAULT PARTITIONS (64 partitions)
-- =============================================================================

DECLARE @i INT = 0;
WHILE @i < 64
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.OutboxPartitions WHERE OutboxTableName = N'Outbox' AND PartitionId = @i)
    BEGIN
        INSERT INTO dbo.OutboxPartitions (OutboxTableName, PartitionId, OwnerPublisherId, OwnedSinceUtc, GraceExpiresUtc)
        VALUES (N'Outbox', @i, NULL, NULL, NULL);
    END;
    SET @i = @i + 1;
END;
GO
