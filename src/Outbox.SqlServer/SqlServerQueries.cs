// Copyright (c) OrgName. All rights reserved.

namespace Outbox.SqlServer;

/// <summary>
///     Precomputed SQL query strings for the SQL Server outbox store and dead-letter manager.
///     Built once per store/manager instance to avoid repeated string interpolation.
/// </summary>
internal sealed class SqlServerQueries
{
    public string TvpType { get; }

    // Store queries
    public string RegisterPublisher { get; }
    public string UnregisterPublisher { get; }
    public string FetchBatch { get; }
    public string DeletePublished { get; }
    public string DeadLetter { get; }
    public string Heartbeat { get; }
    public string GetTotalPartitions { get; }
    public string GetOwnedPartitions { get; }
    public string Rebalance { get; }
    public string ClaimOrphanPartitions { get; }
    public string GetPendingCount { get; }

    // Dead-letter manager queries
    public string DeadLetterGet { get; }
    public string DeadLetterReplay { get; }
    public string DeadLetterPurge { get; }
    public string DeadLetterPurgeAll { get; }

    public SqlServerQueries(string schemaName, string tablePrefix, string sharedSchemaName, string outboxTableName)
    {
        var s = schemaName;
        var p = tablePrefix;
        var outboxTable = $"{s}.{p}Outbox";
        var deadLetterTable = $"{s}.{p}OutboxDeadLetter";
        var publishersTable = $"{sharedSchemaName}.OutboxPublishers";
        var partitionsTable = $"{sharedSchemaName}.OutboxPartitions";
        TvpType = $"{s}.{p}SequenceNumberList";

        RegisterPublisher = $"""
                            MERGE {publishersTable} WITH (HOLDLOCK) AS target
                            USING (SELECT @PublisherId AS PublisherId, @OutboxTableName AS OutboxTableName, @HostName AS HostName) AS source
                                ON target.OutboxTableName = source.OutboxTableName AND target.PublisherId = source.PublisherId
                            WHEN MATCHED THEN
                                UPDATE SET LastHeartbeatUtc = SYSUTCDATETIME(),
                                           HostName         = source.HostName
                            WHEN NOT MATCHED THEN
                                INSERT (PublisherId, OutboxTableName, RegisteredAtUtc, LastHeartbeatUtc, HostName)
                                VALUES (source.PublisherId, source.OutboxTableName, SYSUTCDATETIME(), SYSUTCDATETIME(), source.HostName);
                            """;

        UnregisterPublisher = $"""
                              UPDATE {partitionsTable}
                              SET    OwnerPublisherId = NULL,
                                     OwnedSinceUtc  = NULL,
                                     GraceExpiresUtc = NULL
                              WHERE  OwnerPublisherId = @PublisherId
                                AND  OutboxTableName = @OutboxTableName;

                              DELETE FROM {publishersTable}
                              WHERE  PublisherId = @PublisherId
                                AND  OutboxTableName = @OutboxTableName;
                              """;

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
                           AND op.GraceExpiresUtc IS NULL
                     )
                       AND o.RowVersion < MIN_ACTIVE_ROWVERSION()
                     ORDER BY o.PartitionId, o.SequenceNumber;
                     """;

        DeletePublished = $"""
                           DELETE FROM {outboxTable}
                           WHERE SequenceNumber IN (SELECT SequenceNumber FROM @Ids);
                           """;

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

        Heartbeat = $"""
                     UPDATE {publishersTable}
                     SET    LastHeartbeatUtc = SYSUTCDATETIME()
                     WHERE  PublisherId = @PublisherId
                       AND  OutboxTableName = @OutboxTableName;

                     UPDATE {partitionsTable}
                     SET    GraceExpiresUtc = NULL
                     WHERE  OwnerPublisherId = @PublisherId
                       AND  OutboxTableName = @OutboxTableName
                       AND  GraceExpiresUtc IS NOT NULL;
                     """;

        GetTotalPartitions = $"SELECT COUNT(*) FROM {partitionsTable} WHERE OutboxTableName = @OutboxTableName;";

        GetOwnedPartitions = $"""
                              SELECT PartitionId
                              FROM   {partitionsTable}
                              WHERE  OwnerPublisherId = @PublisherId
                                AND  OutboxTableName = @OutboxTableName;
                              """;

        Rebalance = $"""
                     DECLARE @TotalPartitions   INT;
                     DECLARE @ActivePublishers   INT;
                     DECLARE @FairShare         INT;
                     DECLARE @CurrentlyOwned    INT;
                     DECLARE @ToAcquire         INT;

                     SELECT @TotalPartitions = COUNT(*) FROM {partitionsTable} WHERE OutboxTableName = @OutboxTableName;

                     SELECT @ActivePublishers = COUNT(*)
                     FROM {publishersTable}
                     WHERE OutboxTableName = @OutboxTableName
                       AND LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

                     SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActivePublishers, 0));

                     SELECT @CurrentlyOwned = COUNT(*)
                     FROM {partitionsTable}
                     WHERE OwnerPublisherId = @PublisherId
                       AND OutboxTableName = @OutboxTableName;

                     SET @ToAcquire = @FairShare - @CurrentlyOwned;

                     UPDATE {partitionsTable}
                     SET    GraceExpiresUtc = DATEADD(SECOND, @PartitionGracePeriodSeconds, SYSUTCDATETIME())
                     WHERE  OwnerPublisherId <> @PublisherId
                       AND  OwnerPublisherId IS NOT NULL
                       AND  GraceExpiresUtc IS NULL
                       AND  OutboxTableName = @OutboxTableName
                       AND  OwnerPublisherId NOT IN
                            (
                                SELECT PublisherId
                                FROM   {publishersTable}
                                WHERE  OutboxTableName = @OutboxTableName
                                  AND  LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
                            );

                     IF @ToAcquire > 0
                     BEGIN
                         ;WITH Available AS (
                             SELECT TOP (@ToAcquire) PartitionId
                             FROM   {partitionsTable} WITH (UPDLOCK, READPAST)
                             WHERE  OutboxTableName = @OutboxTableName
                               AND  (OwnerPublisherId IS NULL
                                     OR GraceExpiresUtc < SYSUTCDATETIME())
                             ORDER BY PartitionId
                         )
                         UPDATE op
                         SET    OwnerPublisherId = @PublisherId,
                                OwnedSinceUtc   = SYSUTCDATETIME(),
                                GraceExpiresUtc = NULL
                         FROM   {partitionsTable} op
                         INNER JOIN Available a ON op.PartitionId = a.PartitionId
                           AND op.OutboxTableName = @OutboxTableName;
                     END;

                     SELECT @CurrentlyOwned = COUNT(*)
                     FROM {partitionsTable}
                     WHERE OwnerPublisherId = @PublisherId
                       AND OutboxTableName = @OutboxTableName;

                     IF @CurrentlyOwned > @FairShare
                     BEGIN
                         DECLARE @ToRelease INT = @CurrentlyOwned - @FairShare;

                         UPDATE op
                         SET    OwnerPublisherId = NULL,
                                OwnedSinceUtc  = NULL,
                                GraceExpiresUtc = NULL
                         FROM   {partitionsTable} op
                         WHERE  op.OutboxTableName = @OutboxTableName
                           AND  op.PartitionId IN (
                                    SELECT TOP (@ToRelease) PartitionId
                                    FROM   {partitionsTable}
                                    WHERE  OwnerPublisherId = @PublisherId
                                      AND  OutboxTableName = @OutboxTableName
                                    ORDER BY PartitionId DESC
                                );
                     END;
                     """;

        ClaimOrphanPartitions = $"""
                                 DECLARE @TotalPartitions   INT;
                                 DECLARE @ActivePublishers   INT;
                                 DECLARE @FairShare         INT;
                                 DECLARE @CurrentlyOwned    INT;
                                 DECLARE @ToAcquire         INT;

                                 SELECT @TotalPartitions = COUNT(*) FROM {partitionsTable} WHERE OutboxTableName = @OutboxTableName;

                                 SELECT @ActivePublishers = COUNT(*)
                                 FROM {publishersTable}
                                 WHERE OutboxTableName = @OutboxTableName
                                   AND LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

                                 SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActivePublishers, 0));

                                 SELECT @CurrentlyOwned = COUNT(*)
                                 FROM {partitionsTable}
                                 WHERE OwnerPublisherId = @PublisherId
                                   AND OutboxTableName = @OutboxTableName;

                                 SET @ToAcquire = @FairShare - @CurrentlyOwned;

                                 IF @ToAcquire > 0
                                 BEGIN
                                     ;WITH Available AS (
                                         SELECT TOP (@ToAcquire) PartitionId
                                         FROM   {partitionsTable} WITH (UPDLOCK, READPAST)
                                         WHERE  OutboxTableName = @OutboxTableName
                                           AND  OwnerPublisherId IS NULL
                                         ORDER BY PartitionId
                                     )
                                     UPDATE op
                                     SET    OwnerPublisherId = @PublisherId,
                                            OwnedSinceUtc   = SYSUTCDATETIME(),
                                            GraceExpiresUtc = NULL
                                     FROM   {partitionsTable} op
                                     INNER JOIN Available a ON op.PartitionId = a.PartitionId
                                       AND op.OutboxTableName = @OutboxTableName;
                                 END;
                                 """;

        GetPendingCount = $"SELECT COUNT_BIG(*) FROM {outboxTable} WITH (NOLOCK);";

        // Dead-letter manager queries
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

        // Atomic DELETE...OUTPUT INTO using DeadLetterSeq (PK) for precise targeting.
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

        DeadLetterPurge = $"""
                           DELETE dl
                           FROM {deadLetterTable} dl
                           INNER JOIN @Ids p ON dl.DeadLetterSeq = p.SequenceNumber;
                           """;

        DeadLetterPurgeAll = $"DELETE FROM {deadLetterTable};";
    }
}
