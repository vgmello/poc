IF DB_ID('OutboxTest') IS NULL
    CREATE DATABASE OutboxTest;
GO

USE OutboxTest;
GO

IF OBJECT_ID('dbo.Outbox') IS NULL
BEGIN
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
END;
GO

IF OBJECT_ID('dbo.OutboxDeadLetter') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxDeadLetter
    (
        DeadLetterSeq    BIGINT IDENTITY(1,1)  NOT NULL,
        SequenceNumber   BIGINT                NOT NULL,
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
END;
GO

IF OBJECT_ID('dbo.OutboxProducers') IS NULL
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

IF OBJECT_ID('dbo.OutboxPartitions') IS NULL
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

IF TYPE_ID('dbo.SequenceNumberList') IS NULL
    CREATE TYPE dbo.SequenceNumberList AS TABLE
    (
        SequenceNumber BIGINT NOT NULL PRIMARY KEY
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Outbox_Unleased')
    CREATE NONCLUSTERED INDEX IX_Outbox_Unleased
    ON dbo.Outbox (EventDateTimeUtc, EventOrdinal)
    INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
    WHERE LeasedUntilUtc IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Outbox_LeaseExpiry')
    CREATE NONCLUSTERED INDEX IX_Outbox_LeaseExpiry
    ON dbo.Outbox (LeasedUntilUtc, EventDateTimeUtc, EventOrdinal)
    INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
    WHERE LeasedUntilUtc IS NOT NULL;
GO

-- Seed 8 work-distribution buckets (independent of Redpanda topic partition count)
DECLARE @i INT = 0;
WHILE @i < 8
BEGIN
    INSERT INTO dbo.OutboxPartitions (PartitionId, OwnerProducerId, OwnedSinceUtc, GraceExpiresUtc)
    SELECT @i, NULL, NULL, NULL
    WHERE NOT EXISTS (SELECT 1 FROM dbo.OutboxPartitions WHERE PartitionId = @i);
    SET @i = @i + 1;
END;
GO
