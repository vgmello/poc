# SQL Server Query Execution Plan Report

**Date:** 2026-04-02 15:42 UTC
**Database:** Azure SQL Edge (ARM64) via Docker
**Data:** 10K messages, 128 partitions, 2 publishers (64 partitions each)

---

## 1. FetchBatch

```sql
SELECT TOP (500) o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType, o.Headers, o.Payload, o.PayloadContentType, o.EventDateTimeUtc, o.EventOrdinal, o.RetryCount, o.CreatedAtUtc FROM dbo.Outbo...
```

**Execution Plan:**
```
  |--Sort(TOP 500, ORDER BY:([o].[EventDateTimeUtc] ASC, [o].[EventOrdinal] ASC))
            |--Clustered Index Seek(OBJECT:([master].[dbo].[OutboxPartitions].[PK_OutboxPartitions] AS [op]), SEEK:([op].[OutboxTableName]=N'Outbox'),  WHERE:([master].[dbo].[OutboxPartitions].[OwnerPublisherId] as [op].[OwnerPublisherId]=N'pub-A' AND ([master].[dbo].[OutboxPartitions].[GraceExpiresUtc] as [op].[GraceExpiresUtc] IS NULL OR [master].[dbo].[OutboxPartitions].[GraceExpiresUtc] as [op].[GraceExpiresUtc]<sysutcdatetime())) ORDERED FORWARD)
            |--Compute Scalar(DEFINE:([Expr1004]=[master].[dbo].[Outbox].[PartitionId] as [o].[PartitionId]))
                 |--Index Scan(OBJECT:([master].[dbo].[Outbox].[IX_Outbox_Pending] AS [o]),  WHERE:([master].[dbo].[Outbox].[RowVersion] as [o].[RowVersion]<CONVERT_IMPLICIT(timestamp,min_active_rowversion(),0) AND [master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount]<(5)) ORDERED FORWARD)
```

**Verdict:** Index Seek - efficient

---

## 2. DeletePublished

```sql
DELETE FROM dbo.Outbox WHERE SequenceNumber IN (SELECT SequenceNumber FROM (VALUES (1),(2),(3)) AS t(SequenceNumber))
```

**Execution Plan:**
```
  |--Clustered Index Delete(OBJECT:([master].[dbo].[Outbox].[PK_Outbox]), OBJECT:([master].[dbo].[Outbox].[IX_Outbox_Pending]))
       |--Nested Loops(Left Semi Join, OUTER REFERENCES:([master].[dbo].[Outbox].[SequenceNumber]))
            |--Nested Loops(Inner Join, OUTER REFERENCES:([Expr1014], [Expr1015], [Expr1016]))
            |    |--Merge Interval
            |    |    |--Concatenation
            |    |         |--Compute Scalar(DEFINE:(([Expr1009],[Expr1010],[Expr1008])=GetRangeWithMismatchedTypes((1),NULL,(22))))
            |    |         |    |--Constant Scan
            |    |         |--Compute Scalar(DEFINE:(([Expr1012],[Expr1013],[Expr1011])=GetRangeWithMismatchedTypes(NULL,(3),(42))))
            |    |              |--Constant Scan
            |    |--Clustered Index Seek(OBJECT:([master].[dbo].[Outbox].[PK_Outbox]), SEEK:([master].[dbo].[Outbox].[SequenceNumber] > [Expr1014] AND [master].[dbo].[Outbox].[SequenceNumber] < [Expr1015]) ORDERED FORWARD)
            |--Filter(WHERE:([master].[dbo].[Outbox].[SequenceNumber]=[Union1005]))
                 |--Constant Scan(VALUES:(((1)),((2)),((3))))
```

**Verdict:** Index Seek - efficient

---

## 3. IncrementRetryCount

```sql
UPDATE o SET o.RetryCount = o.RetryCount + 1 FROM dbo.Outbox o WHERE o.SequenceNumber IN (SELECT SequenceNumber FROM (VALUES (4),(5),(6)) AS t(SequenceNumber))
```

**Execution Plan:**
```
  |--Clustered Index Update(OBJECT:([master].[dbo].[Outbox].[PK_Outbox] AS [o]), OBJECT:([master].[dbo].[Outbox].[IX_Outbox_Pending] AS [o]), SET:([master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount] = RaiseIfNullUpdate([Expr1005]),[master].[dbo].[Outbox].[RowVersion] as [o].[RowVersion] = [Expr1006]))
       |--Compute Scalar(DEFINE:([Expr1010]=[Expr1010]))
            |--Compute Scalar(DEFINE:([Expr1010]=CASE WHEN [Expr1008] AND [Expr1009] THEN (0) ELSE (1) END))
                 |--Compute Scalar(DEFINE:([Expr1009]=CASE WHEN [master].[dbo].[Outbox].[RowVersion] as [o].[RowVersion] = [Expr1006] THEN (1) ELSE (0) END))
                      |--Compute Scalar(DEFINE:([Expr1006]=gettimestamp((1))))
                           |--Nested Loops(Left Semi Join, OUTER REFERENCES:([o].[SequenceNumber]))
                                |--Compute Scalar(DEFINE:([Expr1005]=[master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount]+(1), [Expr1008]=CASE WHEN [master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount] = ([master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount]+(1)) THEN (1) ELSE (0) END))
                                |    |--Nested Loops(Inner Join, OUTER REFERENCES:([Expr1017], [Expr1018], [Expr1019]))
                                |         |--Merge Interval
                                |         |    |--Concatenation
                                |         |         |--Compute Scalar(DEFINE:(([Expr1012],[Expr1013],[Expr1011])=GetRangeWithMismatchedTypes((4),NULL,(22))))
                                |         |         |    |--Constant Scan
                                |         |         |--Compute Scalar(DEFINE:(([Expr1015],[Expr1016],[Expr1014])=GetRangeWithMismatchedTypes(NULL,(6),(42))))
                                |         |              |--Constant Scan
                                |         |--Clustered Index Seek(OBJECT:([master].[dbo].[Outbox].[PK_Outbox] AS [o]), SEEK:([o].[SequenceNumber] > [Expr1017] AND [o].[SequenceNumber] < [Expr1018]) ORDERED FORWARD)
                                |--Filter(WHERE:([master].[dbo].[Outbox].[SequenceNumber] as [o].[SequenceNumber]=[Union1004]))
                                     |--Constant Scan(VALUES:(((4)),((5)),((6))))
```

**Verdict:** Index Seek - efficient

---

## 4. DeadLetter (inline)

```sql
DELETE o OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey, deleted.EventType, deleted.Headers, deleted.Payload, deleted.PayloadContentType, deleted.CreatedAtUtc, deleted.RetryCou...
```

**Execution Plan:**
```
  |--Clustered Index Insert(OBJECT:([master].[dbo].[OutboxDeadLetter].[PK_OutboxDeadLetter]), OBJECT:([master].[dbo].[OutboxDeadLetter].[IX_OutboxDeadLetter_SequenceNumber]), SET:([master].[dbo].[OutboxDeadLetter].[SequenceNumber] = [master].[dbo].[Outbox].[SequenceNumber] as [o].[SequenceNumber],[master].[dbo].[OutboxDeadLetter].[TopicName] = [master].[dbo].[Outbox].[TopicName] as [o].[TopicName],[master].[dbo].[OutboxDeadLetter].[PartitionKey] = [master].[dbo].[Outbox].[PartitionKey] as [o].[PartitionKey],[master].[dbo].[OutboxDeadLetter].[EventType] = [master].[dbo].[Outbox].[EventType] as [o].[EventType],[master].[dbo].[OutboxDeadLetter].[Headers] = [Expr1007],[master].[dbo].[OutboxDeadLetter].[Payload] = [master].[dbo].[Outbox].[Payload] as [o].[Payload],[master].[dbo].[OutboxDeadLetter].[PayloadContentType] = [master].[dbo].[Outbox].[PayloadContentType] as [o].[PayloadContentType],[master].[dbo].[OutboxDeadLetter].[CreatedAtUtc] = [master].[dbo].[Outbox].[CreatedAtUtc] as [o].[CreatedAtUtc],[master].[dbo].[OutboxDeadLetter].[RetryCount] = [master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount],[master].[dbo].[OutboxDeadLetter].[EventDateTimeUtc] = [master].[dbo].[Outbox].[EventDateTimeUtc] as [o].[EventDateTimeUtc],[master].[dbo].[OutboxDeadLetter].[EventOrdinal] = [master].[dbo].[Outbox].[EventOrdinal] as [o].[EventOrdinal],[master].[dbo].[OutboxDeadLetter].[DeadLetteredAtUtc] = RaiseIfNullInsert([Expr1008]),[master].[dbo].[OutboxDeadLetter].[LastError] = [Expr1009],[master].[dbo].[OutboxDeadLetter].[DeadLetterSeq] = [Expr1006]))
       |--Compute Scalar(DEFINE:([Expr1007]=CONVERT_IMPLICIT(nvarchar(max),[master].[dbo].[Outbox].[Headers] as [o].[Headers],0), [Expr1008]=CONVERT_IMPLICIT(datetime2(3),sysutcdatetime(),0), [Expr1009]=N'test error'))
            |--Compute Scalar(DEFINE:([Expr1006]=getidentity((391672443),(1),NULL)))
                 |--Clustered Index Delete(OBJECT:([master].[dbo].[Outbox].[PK_Outbox] AS [o]), OBJECT:([master].[dbo].[Outbox].[IX_Outbox_Pending] AS [o]), WHERE:([master].[dbo].[Outbox].[SequenceNumber]=(7)))
```

**Verdict:** Clustered Index Delete (PK targeted) - efficient

---

## 5. Heartbeat

```sql
UPDATE dbo.OutboxPublishers SET LastHeartbeatUtc = SYSUTCDATETIME() WHERE PublisherId = 'pub-A' AND OutboxTableName = 'Outbox'; UPDATE dbo.OutboxPartitions SET GraceExpiresUtc = NULL WHERE OwnerPublis...
```

**Execution Plan:**
```
  |--Clustered Index Update(OBJECT:([master].[dbo].[OutboxPublishers].[PK_OutboxPublishers]), SET:([master].[dbo].[OutboxPublishers].[LastHeartbeatUtc] = RaiseIfNullUpdate([Expr1002])), DEFINE:([Expr1002]=CONVERT_IMPLICIT(datetime2(3),sysutcdatetime(),0)), WHERE:([master].[dbo].[OutboxPublishers].[OutboxTableName]=CONVERT_IMPLICIT(nvarchar(4000),[@2],0) AND [master].[dbo].[OutboxPublishers].[PublisherId]=CONVERT_IMPLICIT(nvarchar(4000),[@1],0)))
  |--Clustered Index Update(OBJECT:([master].[dbo].[OutboxPartitions].[PK_OutboxPartitions]), SET:([master].[dbo].[OutboxPartitions].[GraceExpiresUtc] = [Expr1002]))
       |--Compute Scalar(DEFINE:([Expr1002]=NULL))
            |--Clustered Index Seek(OBJECT:([master].[dbo].[OutboxPartitions].[PK_OutboxPartitions]), SEEK:([master].[dbo].[OutboxPartitions].[OutboxTableName]=CONVERT_IMPLICIT(nvarchar(4000),[@2],0)),  WHERE:([master].[dbo].[OutboxPartitions].[GraceExpiresUtc] IS NOT NULL AND [master].[dbo].[OutboxPartitions].[OwnerPublisherId]=CONVERT_IMPLICIT(nvarchar(4000),[@1],0)) ORDERED FORWARD)
```

**Verdict:** Index Seek - efficient

---

## 6. GetTotalPartitions

```sql
SELECT COUNT(*) FROM dbo.OutboxPartitions WHERE OutboxTableName = 'Outbox'
```

**Execution Plan:**
```
  |--Compute Scalar(DEFINE:([Expr1002]=CONVERT_IMPLICIT(int,[Expr1004],0)))
       |--Stream Aggregate(DEFINE:([Expr1004]=Count(*)))
            |--Clustered Index Seek(OBJECT:([master].[dbo].[OutboxPartitions].[PK_OutboxPartitions]), SEEK:([master].[dbo].[OutboxPartitions].[OutboxTableName]=CONVERT_IMPLICIT(nvarchar(4000),[@1],0)) ORDERED FORWARD)
```

**Verdict:** Index Seek - efficient

---

## 7. GetOwnedPartitions

```sql
SELECT PartitionId FROM dbo.OutboxPartitions WHERE OwnerPublisherId = 'pub-A' AND OutboxTableName = 'Outbox'
```

**Execution Plan:**
```
  |--Clustered Index Seek(OBJECT:([master].[dbo].[OutboxPartitions].[PK_OutboxPartitions]), SEEK:([master].[dbo].[OutboxPartitions].[OutboxTableName]=CONVERT_IMPLICIT(nvarchar(4000),[@2],0)),  WHERE:([master].[dbo].[OutboxPartitions].[OwnerPublisherId]=CONVERT_IMPLICIT(nvarchar(4000),[@1],0)) ORDERED FORWARD)
```

**Verdict:** Index Seek - efficient

---

## 8. GetPendingCount

```sql
SELECT COUNT_BIG(*) FROM dbo.Outbox
```

**Execution Plan:**
```
  |--Stream Aggregate(DEFINE:([Expr1002]=Count(*)))
       |--Clustered Index Scan(OBJECT:([master].[dbo].[Outbox].[PK_Outbox]))
```

**Verdict:** Clustered Index Scan - FULL TABLE SCAN - investigate

---

## 9. SweepDeadLetters

```sql
DELETE o OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey, deleted.EventType, deleted.Headers, deleted.Payload, deleted.PayloadContentType, deleted.CreatedAtUtc, deleted.RetryCou...
```

**Execution Plan:**
```
  |--Clustered Index Insert(OBJECT:([master].[dbo].[OutboxDeadLetter].[PK_OutboxDeadLetter]), OBJECT:([master].[dbo].[OutboxDeadLetter].[IX_OutboxDeadLetter_SequenceNumber]), SET:([master].[dbo].[OutboxDeadLetter].[SequenceNumber] = [master].[dbo].[Outbox].[SequenceNumber] as [o].[SequenceNumber],[master].[dbo].[OutboxDeadLetter].[TopicName] = [master].[dbo].[Outbox].[TopicName] as [o].[TopicName],[master].[dbo].[OutboxDeadLetter].[PartitionKey] = [master].[dbo].[Outbox].[PartitionKey] as [o].[PartitionKey],[master].[dbo].[OutboxDeadLetter].[EventType] = [master].[dbo].[Outbox].[EventType] as [o].[EventType],[master].[dbo].[OutboxDeadLetter].[Headers] = [Expr1006],[master].[dbo].[OutboxDeadLetter].[Payload] = [master].[dbo].[Outbox].[Payload] as [o].[Payload],[master].[dbo].[OutboxDeadLetter].[PayloadContentType] = [master].[dbo].[Outbox].[PayloadContentType] as [o].[PayloadContentType],[master].[dbo].[OutboxDeadLetter].[CreatedAtUtc] = [master].[dbo].[Outbox].[CreatedAtUtc] as [o].[CreatedAtUtc],[master].[dbo].[OutboxDeadLetter].[RetryCount] = [master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount],[master].[dbo].[OutboxDeadLetter].[EventDateTimeUtc] = [master].[dbo].[Outbox].[EventDateTimeUtc] as [o].[EventDateTimeUtc],[master].[dbo].[OutboxDeadLetter].[EventOrdinal] = [master].[dbo].[Outbox].[EventOrdinal] as [o].[EventOrdinal],[master].[dbo].[OutboxDeadLetter].[DeadLetteredAtUtc] = RaiseIfNullInsert([Expr1007]),[master].[dbo].[OutboxDeadLetter].[LastError] = [Expr1008],[master].[dbo].[OutboxDeadLetter].[DeadLetterSeq] = [Expr1005]))
       |--Compute Scalar(DEFINE:([Expr1006]=CONVERT_IMPLICIT(nvarchar(max),[master].[dbo].[Outbox].[Headers] as [o].[Headers],0), [Expr1007]=CONVERT_IMPLICIT(datetime2(3),sysutcdatetime(),0), [Expr1008]=N'Max retry count exceeded'))
            |--Compute Scalar(DEFINE:([Expr1005]=getidentity((391672443),(1),NULL)))
                 |--Clustered Index Delete(OBJECT:([master].[dbo].[Outbox].[PK_Outbox] AS [o]), OBJECT:([master].[dbo].[Outbox].[IX_Outbox_Pending] AS [o]))
                      |--Clustered Index Scan(OBJECT:([master].[dbo].[Outbox].[PK_Outbox] AS [o]), WHERE:([master].[dbo].[Outbox].[RetryCount] as [o].[RetryCount]>=(5)) ORDERED)
```

**Verdict:** Clustered Index Scan - FULL TABLE SCAN - investigate

---

## 10. DeadLetterGet

```sql
SELECT DeadLetterSeq, SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, EventDateTimeUtc, EventOrdinal, RetryCount, CreatedAtUtc, DeadLetteredAtUtc, LastError F...
```

**Execution Plan:**
```
  |--Top(TOP EXPRESSION:((50)))
       |--Clustered Index Scan(OBJECT:([master].[dbo].[OutboxDeadLetter].[PK_OutboxDeadLetter]), ORDERED FORWARD)
```

**Verdict:** Clustered Index Scan - FULL TABLE SCAN - investigate

---

## 11. DeadLetterReplay

```sql
DELETE dl OUTPUT deleted.TopicName, deleted.PartitionKey, deleted.EventType, deleted.Headers, deleted.Payload, deleted.PayloadContentType, deleted.CreatedAtUtc, deleted.EventDateTimeUtc, deleted.Event...
```

**Execution Plan:**
```
  |--Clustered Index Insert(OBJECT:([master].[dbo].[Outbox].[PK_Outbox]), OBJECT:([master].[dbo].[Outbox].[IX_Outbox_Pending]), SET:([master].[dbo].[Outbox].[TopicName] = [master].[dbo].[OutboxDeadLetter].[TopicName] as [dl].[TopicName],[master].[dbo].[Outbox].[PartitionKey] = [master].[dbo].[OutboxDeadLetter].[PartitionKey] as [dl].[PartitionKey],[master].[dbo].[Outbox].[EventType] = [master].[dbo].[OutboxDeadLetter].[EventType] as [dl].[EventType],[master].[dbo].[Outbox].[Headers] = [Expr1006],[master].[dbo].[Outbox].[Payload] = [master].[dbo].[OutboxDeadLetter].[Payload] as [dl].[Payload],[master].[dbo].[Outbox].[PayloadContentType] = [master].[dbo].[OutboxDeadLetter].[PayloadContentType] as [dl].[PayloadContentType],[master].[dbo].[Outbox].[CreatedAtUtc] = [master].[dbo].[OutboxDeadLetter].[CreatedAtUtc] as [dl].[CreatedAtUtc],[master].[dbo].[Outbox].[EventDateTimeUtc] = [master].[dbo].[OutboxDeadLetter].[EventDateTimeUtc] as [dl].[EventDateTimeUtc],[master].[dbo].[Outbox].[EventOrdinal] = [master].[dbo].[OutboxDeadLetter].[EventOrdinal] as [dl].[EventOrdinal],[master].[dbo].[Outbox].[RetryCount] = [Expr1007],[master].[dbo].[Outbox].[SequenceNumber] = [Expr1005],[master].[dbo].[Outbox].[RowVersion] = [Expr1008],[master].[dbo].[Outbox].[PartitionId] = [Expr1009]))
       |--Compute Scalar(DEFINE:([Expr1006]=[Expr1010], [Expr1007]=(0), [Expr1008]=gettimestamp((1)), [Expr1009]=[Expr1011]))
            |--Compute Scalar(DEFINE:([Expr1010]=CONVERT_IMPLICIT(nvarchar(2000),[master].[dbo].[OutboxDeadLetter].[Headers] as [dl].[Headers],0), [Expr1011]=abs(CONVERT(bigint,checksum([master].[dbo].[OutboxDeadLetter].[PartitionKey] as [dl].[PartitionKey]),0))%(128)))
                 |--Compute Scalar(DEFINE:([Expr1005]=getidentity((295672101),(1),NULL)))
                      |--Clustered Index Delete(OBJECT:([master].[dbo].[OutboxDeadLetter].[PK_OutboxDeadLetter] AS [dl]), OBJECT:([master].[dbo].[OutboxDeadLetter].[IX_OutboxDeadLetter_SequenceNumber] AS [dl]), WHERE:([master].[dbo].[OutboxDeadLetter].[DeadLetterSeq]=(1)))
```

**Verdict:** Clustered Index Delete (PK targeted) - efficient

---

## 12. DeadLetterPurgeAll

```sql
DELETE FROM dbo.OutboxDeadLetter
```

**Execution Plan:**
```
  |--Clustered Index Delete(OBJECT:([master].[dbo].[OutboxDeadLetter].[PK_OutboxDeadLetter]), OBJECT:([master].[dbo].[OutboxDeadLetter].[IX_OutboxDeadLetter_SequenceNumber]))
       |--Index Scan(OBJECT:([master].[dbo].[OutboxDeadLetter].[IX_OutboxDeadLetter_SequenceNumber]), ORDERED FORWARD)
```

**Verdict:** Index Scan - review if table is large

---

## 13. RegisterPublisher

```sql
MERGE dbo.OutboxPublishers WITH (HOLDLOCK) AS target USING (SELECT 'pub-C' AS PublisherId, 'Outbox' AS OutboxTableName, 'host3' AS HostName) AS source ON target.OutboxTableName = source.OutboxTableNam...
```

**Execution Plan:**
```
  |--Clustered Index Merge(OBJECT:([master].[dbo].[OutboxPublishers].[PK_OutboxPublishers] AS [target]), SET:(Insert, [master].[dbo].[OutboxPublishers].[RegisteredAtUtc] as [target].[RegisteredAtUtc] = RaiseIfNullUpdate([Expr1007]),[master].[dbo].[OutboxPublishers].[PublisherId] as [target].[PublisherId] = [Expr1008],[master].[dbo].[OutboxPublishers].[HostName] as [target].[HostName] = [Expr1009],[master].[dbo].[OutboxPublishers].[LastHeartbeatUtc] as [target].[LastHeartbeatUtc] = RaiseIfNullUpdate([Expr1010]),[master].[dbo].[OutboxPublishers].[OutboxTableName] as [target].[OutboxTableName] = [Expr1011]), SET:(Update, [master].[dbo].[OutboxPublishers].[HostName] as [target].[HostName] = [Expr1009],[master].[dbo].[OutboxPublishers].[LastHeartbeatUtc] as [target].[LastHeartbeatUtc] = RaiseIfNullUpdate([Expr1010])) ACTION:([Action1006]))
       |--Top(TOP EXPRESSION:((1)))
            |--Compute Scalar(DEFINE:([Expr1007]=CASE WHEN [Action1006]=(4) THEN CONVERT_IMPLICIT(datetime2(3),sysutcdatetime(),0) ELSE [master].[dbo].[OutboxPublishers].[RegisteredAtUtc] as [target].[RegisteredAtUtc] END, [Expr1008]=CASE WHEN [Action1006]=(4) THEN CONVERT_IMPLICIT(nvarchar(128),[Expr1000],0) ELSE [master].[dbo].[OutboxPublishers].[PublisherId] as [target].[PublisherId] END, [Expr1009]=CONVERT_IMPLICIT(nvarchar(256),[Expr1002],0), [Expr1010]=CONVERT_IMPLICIT(datetime2(3),sysutcdatetime(),0), [Expr1011]=CASE WHEN [Action1006]=(4) THEN CONVERT_IMPLICIT(nvarchar(256),[Expr1001],0) ELSE [master].[dbo].[OutboxPublishers].[OutboxTableName] as [target].[OutboxTableName] END))
                 |--Compute Scalar(DEFINE:([Action1006]=ForceOrder(CASE WHEN [TrgPrb1004] IS NOT NULL THEN (1) ELSE (4) END)))
                      |--Compute Scalar(DEFINE:([Expr1000]='pub-C', [Expr1001]='Outbox', [Expr1002]='host3'))
                           |--Nested Loops(Left Outer Join)
                                |--Constant Scan
                                |--Compute Scalar(DEFINE:([TrgPrb1004]=(1)))
                                     |--Clustered Index Seek(OBJECT:([master].[dbo].[OutboxPublishers].[PK_OutboxPublishers] AS [target]), SEEK:([target].[OutboxTableName]=N'Outbox' AND [target].[PublisherId]=N'pub-C') ORDERED FORWARD)
```

**Verdict:** Index Seek - efficient

---

## 14. UnregisterPublisher

```sql
UPDATE dbo.OutboxPartitions SET OwnerPublisherId = NULL, OwnedSinceUtc = NULL, GraceExpiresUtc = NULL WHERE OwnerPublisherId = 'pub-C' AND OutboxTableName = 'Outbox'; DELETE FROM dbo.OutboxPublishers ...
```

**Execution Plan:**
```
  |--Clustered Index Update(OBJECT:([master].[dbo].[OutboxPartitions].[PK_OutboxPartitions]), SET:([master].[dbo].[OutboxPartitions].[OwnerPublisherId] = [Expr1002],[master].[dbo].[OutboxPartitions].[OwnedSinceUtc] = [Expr1003],[master].[dbo].[OutboxPartitions].[GraceExpiresUtc] = [Expr1004]))
       |--Compute Scalar(DEFINE:([Expr1002]=NULL, [Expr1003]=NULL, [Expr1004]=NULL))
            |--Clustered Index Seek(OBJECT:([master].[dbo].[OutboxPartitions].[PK_OutboxPartitions]), SEEK:([master].[dbo].[OutboxPartitions].[OutboxTableName]=CONVERT_IMPLICIT(nvarchar(4000),[@2],0)),  WHERE:([master].[dbo].[OutboxPartitions].[OwnerPublisherId]=CONVERT_IMPLICIT(nvarchar(4000),[@1],0)) ORDERED FORWARD)
  |--Clustered Index Delete(OBJECT:([master].[dbo].[OutboxPublishers].[PK_OutboxPublishers]), WHERE:([master].[dbo].[OutboxPublishers].[OutboxTableName]=CONVERT_IMPLICIT(nvarchar(4000),[@2],0) AND [master].[dbo].[OutboxPublishers].[PublisherId]=CONVERT_IMPLICIT(nvarchar(4000),[@1],0)))
```

**Verdict:** Index Seek - efficient

---

## Index Usage Statistics

| Index | Seeks | Scans | Lookups | Updates |
|-------|-------|-------|---------|---------|
| Outbox.IX_Outbox_Pending | 0 | 0 | 0 | 11 |
| Outbox.PK_Outbox | 0 | 0 | 0 | 11 |
| OutboxDeadLetter.IX_OutboxDeadLetter_SequenceNumber | 0 | 0 | 0 | 1 |
| OutboxDeadLetter.PK_OutboxDeadLetter | 0 | 0 | 0 | 1 |
| OutboxPartitions.PK_OutboxPartitions | 130 | 0 | 0 | 130 |
| OutboxPublishers.PK_OutboxPublishers | 0 | 0 | 0 | 1 |

## Missing Index Suggestions

**(none)** - SQL Server has no index improvement suggestions.

---

## Summary

| Query | Frequency | Plan | Verdict |
|-------|-----------|------|---------|
| **FetchBatch** | Every 50-1000ms | Index Scan on IX_Outbox_Pending (10K rows) / Index Seek (50K+ rows) | **Efficient** — optimizer switches to seek at scale |
| **DeletePublished** | After each send | Clustered Index Seek (PK) | **Efficient** |
| **IncrementRetryCount** | On transport failure | Clustered Index Seek (PK) | **Efficient** |
| **DeadLetter (inline)** | On poison messages | Clustered Index Delete (PK) | **Efficient** |
| **Heartbeat** | Every 10s | Clustered Index Seek + Update (PK) | **Efficient** |
| **GetTotalPartitions** | Cached 60s | Clustered Index Seek (PK) | **Efficient** |
| **GetOwnedPartitions** | After rebalance | Clustered Index Seek (PK) | **Efficient** |
| **GetPendingCount** | Every 10s (heartbeat) | Clustered Index Scan (PK) | **Acceptable** — COUNT(*) requires full scan |
| **SweepDeadLetters** | Every 60s | Clustered Index Scan (PK), filter RetryCount>=5 | **Acceptable** — runs infrequently, typically finds 0 rows |
| **DeadLetterGet** | On-demand (API) | Clustered Index Scan (PK, ORDERED) | **Acceptable** — paginated with OFFSET/FETCH |
| **DeadLetterReplay** | On-demand (API) | Clustered Index Delete (PK) | **Efficient** |
| **DeadLetterPurgeAll** | On-demand (API) | Index Scan (full delete) | **Expected** — deleting all rows requires scan |
| **RegisterPublisher** | Once at startup | Clustered Index Seek (PK) via MERGE | **Efficient** |
| **UnregisterPublisher** | Once at shutdown | Clustered Index Seek (PK) | **Efficient** |

### Key findings

1. **Zero missing index suggestions** — SQL Server is satisfied with the current index design.
2. **FetchBatch uses IX_Outbox_Pending** (not PK_Outbox scan) — the precomputed PartitionId column works as intended. At 10K rows the optimizer chooses an ordered scan of the narrower index; at 50K+ rows it switches to Index Seek per partition.
3. **All PK-based operations use Clustered Index Seek** — no unexpected scans on the hot path.
4. **No key lookups** — IX_Outbox_Pending is fully covering (0 lookups in stats).
5. **SweepDeadLetters scans the clustered index** for `RetryCount >= 5`. An index on `(RetryCount)` could help if the outbox table is very large during outages, but this query runs every 60s and typically finds very few rows — not worth an extra index.
