# EventHub Outbox Pattern — Audit Findings and Fix Design

## Context

Deep technical audit of the EventHub outbox pattern implementation across three artifacts:
- `EventHubOutboxSpec.md` (architecture spec)
- `EventHubOutbox.sql` (schema, indexes, queries)
- `OutboxPublisher.cs` (C# publisher)

The audit found 4 critical and 12 important issues. This document captures each finding and its proposed fix.

---

## Critical Issues

### C1: PartitionKey never set on EventHub batch — ordering broken

**Problem:** `BuildEventHubBatchesAsync` creates batches with `new CreateBatchOptions { MaximumSizeInBytes = ... }` but never sets `PartitionKey`. EventHub uses round-robin routing, making the entire partition affinity SQL machinery (OutboxPartitions, CHECKSUM join, grace periods, rebalancing) ineffective. The ordering guarantee in spec sections 3 and 4 does not hold end-to-end.

**Fix:** Group rows by `PartitionKey` within each topic before batching. Create each `EventDataBatch` with the partition key set:

```csharp
var byTopicAndKey = rows.GroupBy(r => (r.TopicName, r.PartitionKey));

foreach (var group in byTopicAndKey)
{
    var options = new CreateBatchOptions
    {
        MaximumSizeInBytes = _options.EventHubMaxBatchBytes,
        PartitionKey = group.Key.PartitionKey
    };
    // build batch with these options...
}
```

This requires restructuring `PublishBatchAsync` to group by `(TopicName, PartitionKey)` instead of just `TopicName`.

### C2: Dead-letter sweep INSERT/DELETE not atomic — data loss

**Problem:** The INSERT uses `READPAST` (skips locked rows) but the DELETE does not. Between the two statements, a row skipped by INSERT can become unlocked and be DELETEd without a corresponding DeadLetter entry. Concurrent sweeps can also produce duplicate dead-letter entries since `OutboxDeadLetter` has no unique constraint on `SequenceNumber`.

**Fix:** Use a single DELETE with OUTPUT INTO to make the operation atomic:

```sql
BEGIN TRANSACTION;
    DELETE o
    OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
           deleted.EventType, deleted.Headers, deleted.Payload,
           deleted.CreatedAtUtc, deleted.RetryCount, SYSUTCDATETIME(), @LastError
    INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
         Headers, Payload, CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    WHERE RetryCount >= @MaxRetryCount
      AND (LeasedUntilUtc IS NULL OR LeasedUntilUtc < SYSUTCDATETIME());
COMMIT TRANSACTION;
```

Apply the same fix to both `EventHubOutbox.sql` section 4e and `OutboxPublisher.cs` `SweepDeadLettersAsync`.

### C3: Orphan sweep missing from C#

**Problem:** Spec sections 4 and 11 describe an orphan sweep that runs every `@OrphanSweepIntervalSeconds` to claim unowned partitions and process their rows. The SQL file has a stub (6e) but no complete query. The C# code has no orphan sweep loop and no `OrphanSweepIntervalSeconds` option. Rows for unowned partitions accumulate without bound.

**Fix:**
1. Add `OrphanSweepIntervalMs` to `OutboxPublisherOptions` (default: 60000).
2. Add `OrphanSweepLoopAsync` that periodically identifies unowned partitions with pending rows and claims them via the existing rebalance claim query.
3. Complete the SQL stub in section 6e.

### C4: No circuit breaker — EventHub outage causes data loss

**Problem:** During EventHub outages, the publisher continues leasing rows, failing to send, and letting leases expire. The recovery path increments `RetryCount` each cycle. After `MaxRetryCount=5` recoveries (~6 minutes with defaults), messages are dead-lettered permanently. This violates the at-least-once delivery requirement.

**Fix:** Add a simple circuit breaker to `OutboxPublisher`:
- Track consecutive EventHub send failures per topic.
- After N consecutive failures (e.g., 3), open the circuit: pause polling for that topic for a configurable duration (e.g., 30s).
- After the pause, attempt a single send (half-open). If it succeeds, close the circuit and resume normal polling. If it fails, extend the pause.
- While the circuit is open, do NOT lease new rows (so retry counts are not burned).
- Surface circuit state via the `OnError` callback for health check integration.

---

## Important Issues

### I1: StopAsync doesn't unregister on WhenAll failure

**Problem:** If `Task.WhenAll` throws, `UnregisterProducerAsync` is skipped. Partitions are orphaned for up to 90s (heartbeat timeout + grace period).

**Fix:** Wrap `WhenAll` in try/finally, call `UnregisterProducerAsync` in the finally block.

### I2: MERGE without HOLDLOCK

**Problem:** Concurrent MERGE for the same ProducerId can race between MATCH evaluation and INSERT, causing a PK violation. This is a documented SQL Server issue.

**Fix:** Add `WITH (HOLDLOCK)` to the MERGE target in both SQL and C#:
```sql
MERGE dbo.OutboxProducers WITH (HOLDLOCK) AS target ...
```

### I3: No startup validation for partition count

**Problem:** If `dbo.OutboxPartitions` is empty, `_totalPartitionCount = 0` and the publish loop silently does nothing.

**Fix:** Assert `_totalPartitionCount > 0` after `GetTotalPartitionCountAsync` in `StartAsync`. Throw `InvalidOperationException` with a clear message.

### I4: RetryCount and CreatedAtUtc missing from index INCLUDEs

**Problem:** Recovery poll filters on `RetryCount < @MaxRetryCount` but `RetryCount` is not in any nonclustered index. Every candidate row requires a clustered index key lookup.

**Fix:** Add `RetryCount` and `CreatedAtUtc` to the INCLUDE lists of both `IX_Outbox_Unleased` and `IX_Outbox_LeaseExpiry`.

### I5: No validation of PartitionGracePeriodSeconds > LeaseDurationSeconds

**Problem:** If an operator sets `PartitionGracePeriodSeconds <= LeaseDurationSeconds`, the new partition owner can start polling while the old owner's leases are still active, causing duplicate sends and broken ordering.

**Fix:** Add validation in `StartAsync`:
```csharp
if (_options.PartitionGracePeriodSeconds <= _options.LeaseDurationSeconds)
    throw new InvalidOperationException(
        "PartitionGracePeriodSeconds must exceed LeaseDurationSeconds.");
```

### I6: lastError always null in dead-letter records

**Problem:** Both `RecoveryLoopAsync` and `DeadLetterSweepLoopAsync` pass `lastError: null`. The `OutboxDeadLetter.LastError` column is never populated, making dead-lettered rows undiagnosable.

**Fix:** Capture the exception message from failed EventHub sends and propagate it through to the dead-letter path. Store the last error per row (e.g., in a dictionary keyed by SequenceNumber) during `PublishBatchAsync` and pass it to the sweep.

### I7: Monitoring query CASE order misclassifies orphaned partitions

**Problem:** A partition whose producer crashed but still has a future `GraceExpiresUtc` is classified as 'IN_GRACE' instead of 'ORPHANED' because the CASE evaluates grace before checking if the producer exists.

**Fix:** Reorder the CASE: check UNOWNED first, then ORPHANED (`pr.ProducerId IS NULL`), then IN_GRACE.

### I8: EventDataBatch leak on CreateBatchAsync failure

**Problem:** If `CreateBatchAsync` throws when opening a new batch mid-loop, the partially-built batch list leaks undisposed batches.

**Fix:** Wrap the batch-building loop in try/finally that disposes `currentBatch` if it hasn't been transferred to the result list.

### I9: IX_Outbox_Partition is a dead index

**Problem:** The index comment claims seek capability for partition affinity queries, but the actual query uses `ABS(CHECKSUM(PartitionKey)) % @TotalPartitions` which is non-SARGable. The index provides no benefit but costs write amplification.

**Fix:** Either remove the index (saving 2 index ops per message lifecycle) or add a persisted computed column `PartitionBucket` to enable a genuine seek. For this PoC, removing is simpler.

### I10: Spec says FLOOR, implementation uses CEILING

**Problem:** Spec section 4 says `FLOOR(TotalPartitions / ActiveProducers)`. SQL and C# correctly use `CEILING`. FLOOR would leave partitions unowned.

**Fix:** Correct the spec text to say CEILING.

### I11: Legacy System.Data.SqlClient

**Problem:** `System.Data.SqlClient` is frozen since 2018. Lacks Azure AD/managed identity auth, TLS 1.3, connection resiliency improvements.

**Fix:** Replace with `Microsoft.Data.SqlClient`. The API surface is identical; only the namespace and NuGet package change.

### I12: Adaptive backoff fields not volatile

**Problem:** `_consecutiveEmptyPolls` and `_currentPollIntervalMs` are plain `int` fields read/written across async continuations that may resume on different threads.

**Fix:** Mark both fields as `volatile` or use `Interlocked` operations.

---

## Out of Scope

- Building a runnable .NET solution with project files, DI, hosted service
- Consumer-side inbox pattern
- Integration tests against real SQL Server / EventHub
- Metrics/observability beyond the existing monitoring queries
