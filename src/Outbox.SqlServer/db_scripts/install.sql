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
        Headers          NVARCHAR(4000)        NULL,
        Payload          NVARCHAR(4000)        NOT NULL,
        CreatedAtUtc     DATETIME2(3)          NOT NULL  DEFAULT SYSUTCDATETIME(),
        EventDateTimeUtc DATETIME2(3)          NOT NULL,
        EventOrdinal     SMALLINT              NOT NULL  DEFAULT 0,
        LeasedUntilUtc   DATETIME2(3)          NULL,
        LeaseOwner       NVARCHAR(128)         NULL,
        RetryCount       INT                   NOT NULL  DEFAULT 0,

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
        Headers           NVARCHAR(4000)        NULL,
        Payload           NVARCHAR(4000)        NOT NULL,
        CreatedAtUtc      DATETIME2(3)          NOT NULL,
        RetryCount        INT                   NOT NULL,
        EventDateTimeUtc  DATETIME2(3)          NOT NULL,
        EventOrdinal      SMALLINT              NOT NULL  DEFAULT 0,
        DeadLetteredAtUtc DATETIME2(3)          NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastError         NVARCHAR(2000)        NULL,

        CONSTRAINT PK_OutboxDeadLetter PRIMARY KEY CLUSTERED (DeadLetterSeq)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- 1c. OutboxProducers — heartbeat registry for active publisher instances
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.OutboxProducers') AND type = N'U')
BEGIN
    CREATE TABLE dbo.OutboxProducers
    (
        ProducerId        NVARCHAR(128)  NOT NULL,
        RegisteredAtUtc   DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastHeartbeatUtc  DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
        HostName          NVARCHAR(256)  NULL,

        CONSTRAINT PK_OutboxProducers PRIMARY KEY CLUSTERED (ProducerId)
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
        PartitionId        INT            NOT NULL,
        OwnerProducerId    NVARCHAR(128)  NULL,
        OwnedSinceUtc      DATETIME2(3)   NULL,
        GraceExpiresUtc    DATETIME2(3)   NULL,

        CONSTRAINT PK_OutboxPartitions PRIMARY KEY CLUSTERED (PartitionId)
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

-- Unified poll (fresh rows arm): unleased rows in causal order
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Outbox') AND name = N'IX_Outbox_Unleased')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Outbox_Unleased
    ON dbo.Outbox (EventDateTimeUtc, EventOrdinal)
    INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
    WHERE LeasedUntilUtc IS NULL;
END;
GO

-- Unified poll (expired rows arm): expired-lease rows
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Outbox') AND name = N'IX_Outbox_LeaseExpiry')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Outbox_LeaseExpiry
    ON dbo.Outbox (LeasedUntilUtc, EventDateTimeUtc, EventOrdinal)
    INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
    WHERE LeasedUntilUtc IS NOT NULL;
END;
GO

-- =============================================================================
-- SECTION 4: SEED DEFAULT PARTITIONS (32 partitions)
-- =============================================================================

DECLARE @i INT = 0;
WHILE @i < 32
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.OutboxPartitions WHERE PartitionId = @i)
    BEGIN
        INSERT INTO dbo.OutboxPartitions (PartitionId, OwnerProducerId, OwnedSinceUtc, GraceExpiresUtc)
        VALUES (@i, NULL, NULL, NULL);
    END;
    SET @i = @i + 1;
END;
GO
