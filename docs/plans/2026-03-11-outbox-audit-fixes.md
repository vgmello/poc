# EventHub Outbox Audit Fixes — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all critical and important issues identified in the deep technical audit of the EventHub outbox pattern.

**Architecture:** The codebase is three standalone files (spec, SQL, C#) with no build system. Fixes are direct edits to these files, grouped by logical dependency. Critical issues (C1-C4) are addressed first, then important issues (I1-I12).

**Tech Stack:** C# (.NET), SQL Server T-SQL, Azure EventHub SDK

**Note:** This project has no .csproj, .sln, or test infrastructure. Each task produces a compilable-in-context edit verified by reading the changed code. Commits are frequent to allow easy bisection.

---

### Task 1: Fix PartitionKey not set on EventHub batch (C1)

The most impactful bug: EventHub ordering is completely broken because `CreateBatchOptions.PartitionKey` is never set.

**Files:**
- Modify: `OutboxPublisher.cs:743-851` (PublishBatchAsync + BuildEventHubBatchesAsync)

**Step 1: Restructure PublishBatchAsync to group by (TopicName, PartitionKey)**

Change the grouping from `rows.GroupBy(r => r.TopicName)` to `rows.GroupBy(r => (r.TopicName, r.PartitionKey))`. This ensures each EventHub batch has a single partition key.

Replace `PublishBatchAsync` (lines 743-792) with:

```csharp
private async Task PublishBatchAsync(
    IReadOnlyList<OutboxRow> rows, string? lastError, CancellationToken ct)
{
    var byTopicAndKey = rows.GroupBy(r => (r.TopicName, r.PartitionKey));
    var published = new List<long>(rows.Count);

    foreach (var group in byTopicAndKey)
    {
        string topicName = group.Key.TopicName;
        string partitionKey = group.Key.PartitionKey;
        try
        {
            EventHubProducerClient producer = await GetOrCreateProducerAsync(topicName, ct)
                .ConfigureAwait(false);

            var batches = await BuildEventHubBatchesAsync(producer, partitionKey, group, ct)
                .ConfigureAwait(false);

            foreach ((EventDataBatch eventBatch, List<long> batchSequenceNumbers) in batches)
            {
                await using (eventBatch)
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sendCts.CancelAfter(TimeSpan.FromSeconds(_options.EventHubSendTimeoutSeconds));

                    try
                    {
                        await producer.SendAsync(eventBatch, sendCts.Token).ConfigureAwait(false);
                        published.AddRange(batchSequenceNumbers);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        OnError($"EventHub send timeout for topic '{topicName}' key '{partitionKey}'", null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OnError($"EventHub publish error for topic '{topicName}' key '{partitionKey}'", ex);
        }
    }

    if (published.Count > 0)
    {
        await DeletePublishedRowsAsync(published, ct).ConfigureAwait(false);
    }
}
```

**Step 2: Update BuildEventHubBatchesAsync to accept and use partitionKey**

Replace the signature and first `CreateBatchAsync` call (lines 801-810) to pass `PartitionKey` in `CreateBatchOptions`:

```csharp
private async Task<List<(EventDataBatch Batch, List<long> SequenceNumbers)>> BuildEventHubBatchesAsync(
    EventHubProducerClient producer,
    string partitionKey,
    IEnumerable<OutboxRow> rows,
    CancellationToken ct)
{
    var result = new List<(EventDataBatch, List<long>)>();
    var batchOptions = new CreateBatchOptions
    {
        MaximumSizeInBytes = _options.EventHubMaxBatchBytes,
        PartitionKey = partitionKey
    };

    EventDataBatch? currentBatch = await producer
        .CreateBatchAsync(batchOptions, ct)
        .ConfigureAwait(false);
    var currentIds = new List<long>();
```

Also update the second `CreateBatchAsync` call inside the loop (line 825-827) to use the same `batchOptions`:

```csharp
currentBatch = await producer
    .CreateBatchAsync(batchOptions, ct)
    .ConfigureAwait(false);
```

**Step 3: Verify the change by reading the modified file**

Read `OutboxPublisher.cs` to confirm `PartitionKey` is set on all `CreateBatchOptions` instances and the grouping is by `(TopicName, PartitionKey)`.

**Step 4: Commit**

```bash
git add OutboxPublisher.cs
git commit -m "fix: set PartitionKey on EventHub batches for ordered delivery (C1)"
```

---

### Task 2: Fix dead-letter sweep atomicity (C2)

The INSERT+DELETE in the dead-letter sweep can operate on different row sets. Replace with a single DELETE...OUTPUT INTO pattern.

**Files:**
- Modify: `OutboxPublisher.cs:455-487` (SweepDeadLettersAsync)
- Modify: `EventHubOutbox.sql:315-337` (section 4e)

**Step 1: Fix the C# SweepDeadLettersAsync SQL**

Replace the SQL string in `SweepDeadLettersAsync` (lines 457-475) with:

```csharp
const string sql = @"
BEGIN TRANSACTION;

    DELETE o
    OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
           deleted.EventType, deleted.Headers, deleted.Payload,
           deleted.CreatedAtUtc, deleted.RetryCount, SYSUTCDATETIME(), @LastError
    INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
         Headers, Payload, CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    WHERE o.RetryCount >= @MaxRetryCount
      AND (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME());

COMMIT TRANSACTION;";
```

**Step 2: Fix the SQL file section 4e to match**

Replace lines 319-337 in `EventHubOutbox.sql` with the same DELETE...OUTPUT INTO pattern (inside a comment block like the other queries).

**Step 3: Verify consistency between C# and SQL file**

Read both files to confirm the dead-letter sweep query is identical.

**Step 4: Commit**

```bash
git add OutboxPublisher.cs EventHubOutbox.sql
git commit -m "fix: make dead-letter sweep atomic with DELETE...OUTPUT INTO (C2)"
```

---

### Task 3: Fix EventDataBatch leak (I8)

While we're in `BuildEventHubBatchesAsync`, fix the batch leak when `CreateBatchAsync` throws mid-loop.

**Files:**
- Modify: `OutboxPublisher.cs:801-851` (BuildEventHubBatchesAsync)

**Step 1: Wrap the batch-building loop in try/finally**

After the changes from Task 1, the method body should be wrapped so `currentBatch` is disposed on exception:

```csharp
{
    var result = new List<(EventDataBatch, List<long>)>();
    var batchOptions = new CreateBatchOptions
    {
        MaximumSizeInBytes = _options.EventHubMaxBatchBytes,
        PartitionKey = partitionKey
    };

    EventDataBatch? currentBatch = null;
    var currentIds = new List<long>();

    try
    {
        currentBatch = await producer
            .CreateBatchAsync(batchOptions, ct)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            EventData eventData = BuildEventData(row);

            if (!currentBatch.TryAdd(eventData))
            {
                if (currentBatch.Count > 0)
                {
                    result.Add((currentBatch, currentIds));
                    currentBatch = null;
                }
                else
                {
                    currentBatch.Dispose();
                    currentBatch = null;
                }

                currentBatch = await producer
                    .CreateBatchAsync(batchOptions, ct)
                    .ConfigureAwait(false);
                currentIds = new List<long>();

                if (!currentBatch.TryAdd(eventData))
                {
                    await DeadLetterSingleRowAsync(row,
                        $"Message SequenceNumber={row.SequenceNumber} exceeds EventHub max batch size ({_options.EventHubMaxBatchBytes} bytes)",
                        ct).ConfigureAwait(false);
                    continue;
                }
            }

            currentIds.Add(row.SequenceNumber);
        }

        if (currentBatch is not null && currentBatch.Count > 0)
        {
            result.Add((currentBatch, currentIds));
            currentBatch = null;
        }
    }
    finally
    {
        currentBatch?.Dispose();
    }

    return result;
}
```

**Step 2: Commit**

```bash
git add OutboxPublisher.cs
git commit -m "fix: dispose EventDataBatch on CreateBatchAsync failure (I8)"
```

---

### Task 4: Fix StopAsync unregister in finally (I1)

**Files:**
- Modify: `OutboxPublisher.cs:119-131` (StopAsync)

**Step 1: Wrap WhenAll in try/finally**

Replace `StopAsync`:

```csharp
public async Task StopAsync(CancellationToken cancellationToken = default)
{
    _cts.Cancel();

    try
    {
        await Task.WhenAll(
            _publishLoop,
            _recoveryLoop,
            _heartbeatLoop,
            _deadLetterSweepLoop,
            _rebalanceLoop).ConfigureAwait(false);
    }
    finally
    {
        await UnregisterProducerAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

**Step 2: Commit**

```bash
git add OutboxPublisher.cs
git commit -m "fix: unregister producer in finally block during shutdown (I1)"
```

---

### Task 5: Fix MERGE race condition (I2)

**Files:**
- Modify: `OutboxPublisher.cs:528-537` (RegisterProducerAsync SQL)
- Modify: `EventHubOutbox.sql:353-366` (section 5a)

**Step 1: Add HOLDLOCK to MERGE in C#**

Change `MERGE dbo.OutboxProducers AS target` to `MERGE dbo.OutboxProducers WITH (HOLDLOCK) AS target` in `RegisterProducerAsync`.

**Step 2: Add HOLDLOCK to MERGE in SQL file**

Same change in `EventHubOutbox.sql` section 5a.

**Step 3: Commit**

```bash
git add OutboxPublisher.cs EventHubOutbox.sql
git commit -m "fix: add HOLDLOCK to MERGE to prevent concurrent registration race (I2)"
```

---

### Task 6: Add startup validation (I3, I5)

**Files:**
- Modify: `OutboxPublisher.cs:98-113` (StartAsync)

**Step 1: Add partition count and option validation to StartAsync**

After line 105 (`_totalPartitionCount = await GetTotalPartitionCountAsync(...)`) add:

```csharp
if (_totalPartitionCount == 0)
    throw new InvalidOperationException(
        "dbo.OutboxPartitions is empty. Run the partition initialisation script (EventHubOutbox.sql §6a) before starting publishers.");

if (_options.PartitionGracePeriodSeconds <= _options.LeaseDurationSeconds)
    throw new InvalidOperationException(
        $"PartitionGracePeriodSeconds ({_options.PartitionGracePeriodSeconds}) must exceed LeaseDurationSeconds ({_options.LeaseDurationSeconds}) to prevent ordering violations during partition handover.");
```

The option validation should go before the async calls (at the start of StartAsync), since it doesn't need DB access:

```csharp
public async Task StartAsync(CancellationToken cancellationToken = default)
{
    if (_options.PartitionGracePeriodSeconds <= _options.LeaseDurationSeconds)
        throw new InvalidOperationException(
            $"PartitionGracePeriodSeconds ({_options.PartitionGracePeriodSeconds}) must exceed LeaseDurationSeconds ({_options.LeaseDurationSeconds}) to prevent ordering violations during partition handover.");

    _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    await RegisterProducerAsync(_cts.Token).ConfigureAwait(false);

    _totalPartitionCount = await GetTotalPartitionCountAsync(_cts.Token).ConfigureAwait(false);
    if (_totalPartitionCount == 0)
        throw new InvalidOperationException(
            "dbo.OutboxPartitions is empty. Run the partition initialisation script (EventHubOutbox.sql §6a) before starting publishers.");

    // ... rest unchanged
```

**Step 2: Commit**

```bash
git add OutboxPublisher.cs
git commit -m "fix: validate partition count and grace period on startup (I3, I5)"
```

---

### Task 7: Fix index INCLUDEs (I4) and remove dead index (I9)

**Files:**
- Modify: `EventHubOutbox.sql:114-140` (section 3 indexes)

**Step 1: Add RetryCount and CreatedAtUtc to filtered index INCLUDEs**

Replace `IX_Outbox_Unleased`:

```sql
CREATE NONCLUSTERED INDEX IX_Outbox_Unleased
ON dbo.Outbox (SequenceNumber)
INCLUDE (TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
WHERE LeasedUntilUtc IS NULL;
GO
```

Replace `IX_Outbox_LeaseExpiry`:

```sql
CREATE NONCLUSTERED INDEX IX_Outbox_LeaseExpiry
ON dbo.Outbox (LeasedUntilUtc, SequenceNumber)
INCLUDE (TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
WHERE LeasedUntilUtc IS NOT NULL;
GO
```

**Step 2: Remove IX_Outbox_Partition**

Delete lines 132-140 (the `IX_Outbox_Partition` index and its comment). The CHECKSUM-based partition affinity query is non-SARGable on PartitionKey so this index provides no seek benefit.

**Step 3: Update the spec index section (§10)**

In `EventHubOutboxSpec.md`, update the index definitions to match the new INCLUDEs. Remove the `IX_Outbox_Partition` section. Update the write amplification table from 6 to 4 index operations per message.

**Step 4: Commit**

```bash
git add EventHubOutbox.sql EventHubOutboxSpec.md
git commit -m "fix: add RetryCount/CreatedAtUtc to index INCLUDEs, remove dead IX_Outbox_Partition (I4, I9)"
```

---

### Task 8: Fix monitoring query CASE order (I7)

**Files:**
- Modify: `EventHubOutbox.sql:620-637` (section 7d)
- Modify: `EventHubOutboxSpec.md:481-489` (§12 partition ownership query)

**Step 1: Reorder CASE in SQL file**

Replace the CASE expression in section 7d:

```sql
    CASE
        WHEN p.OwnerProducerId IS NULL THEN 'UNOWNED'
        WHEN pr.ProducerId IS NULL THEN 'ORPHANED'
        WHEN p.GraceExpiresUtc IS NOT NULL
             AND p.GraceExpiresUtc > SYSUTCDATETIME() THEN 'IN_GRACE'
        ELSE 'OWNED'
    END AS Status
```

**Step 2: Update the same query in the spec**

Apply the same reordering to the spec's §12 partition ownership query.

**Step 3: Commit**

```bash
git add EventHubOutbox.sql EventHubOutboxSpec.md
git commit -m "fix: reorder monitoring CASE to detect orphaned partitions before IN_GRACE (I7)"
```

---

### Task 9: Fix spec text errors (I10)

**Files:**
- Modify: `EventHubOutboxSpec.md:144` (§4 FLOOR -> CEILING)

**Step 1: Fix FLOOR to CEILING in spec §4**

In section 4 (line 143-144), change:

> "Each publisher claims `FLOOR(TotalPartitions / ActiveProducers)` partitions."

to:

> "Each publisher claims `CEILING(TotalPartitions / ActiveProducers)` partitions."

This matches the SQL and C# implementation.

**Step 2: Commit**

```bash
git add EventHubOutboxSpec.md
git commit -m "fix: correct spec §4 fair-share formula from FLOOR to CEILING (I10)"
```

---

### Task 10: Fix adaptive backoff thread safety (I12) and legacy SqlClient (I11)

**Files:**
- Modify: `OutboxPublisher.cs:4` (using directive)
- Modify: `OutboxPublisher.cs:63-64` (field declarations)

**Step 1: Replace System.Data.SqlClient with Microsoft.Data.SqlClient**

Change line 4:

```csharp
using Microsoft.Data.SqlClient;
```

**Step 2: Make backoff fields volatile**

Change lines 63-64:

```csharp
private volatile int _consecutiveEmptyPolls = 0;
private volatile int _currentPollIntervalMs;
```

**Step 3: Commit**

```bash
git add OutboxPublisher.cs
git commit -m "fix: use Microsoft.Data.SqlClient, make backoff fields volatile (I11, I12)"
```

---

### Task 11: Add orphan sweep loop (C3)

**Files:**
- Modify: `OutboxPublisher.cs` (add OrphanSweepIntervalMs option, add loop, start it)
- Modify: `EventHubOutbox.sql:560-572` (complete the orphan sweep query stub)

**Step 1: Add OrphanSweepIntervalMs to OutboxPublisherOptions**

Add after the `DeadLetterSweepIntervalMs` property:

```csharp
/// <summary>How often to scan for rows in unowned partitions in milliseconds. Default: 60 000.</summary>
public int OrphanSweepIntervalMs { get; set; } = 60_000;
```

**Step 2: Add _orphanSweepLoop field**

Add to the State section (after `_rebalanceLoop`):

```csharp
private Task _orphanSweepLoop = Task.CompletedTask;
```

**Step 3: Add OrphanSweepLoopAsync method**

Add after `RebalanceLoopAsync`:

```csharp
private async Task OrphanSweepLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await DelayAsync(_options.OrphanSweepIntervalMs, ct).ConfigureAwait(false);
            await ClaimOrphanPartitionsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            OnError("OrphanSweepLoop", ex);
        }
    }
}
```

**Step 4: Add ClaimOrphanPartitionsAsync method**

```csharp
private async Task ClaimOrphanPartitionsAsync(CancellationToken ct)
{
    const string sql = @"
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
    UPDATE TOP (@ToAcquire) dbo.OutboxPartitions WITH (UPDLOCK)
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId IS NULL;
END;";

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct).ConfigureAwait(false);

    await using var cmd = new SqlCommand(sql, conn);
    cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
    cmd.Parameters.AddWithValue("@ProducerId", _producerId);
    cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _options.HeartbeatTimeoutSeconds);

    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

    // Refresh local partition list after claiming orphans.
    const string getOwnedSql = @"
SELECT PartitionId
FROM   dbo.OutboxPartitions
WHERE  OwnerProducerId = @ProducerId2
  AND  (GraceExpiresUtc IS NULL OR GraceExpiresUtc < SYSUTCDATETIME());";

    var owned = new List<int>();
    await using var cmd2 = new SqlCommand(getOwnedSql, conn);
    cmd2.CommandTimeout = _options.SqlCommandTimeoutSeconds;
    cmd2.Parameters.AddWithValue("@ProducerId2", _producerId);

    await using var reader = await cmd2.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
        owned.Add(reader.GetInt32(0));

    await _partitionLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        _ownedPartitions = owned;
    }
    finally
    {
        _partitionLock.Release();
    }
}
```

**Step 5: Start the loop in StartAsync and include in StopAsync**

In `StartAsync`, add after `_deadLetterSweepLoop`:
```csharp
_orphanSweepLoop = OrphanSweepLoopAsync(_cts.Token);
```

In `StopAsync`, add `_orphanSweepLoop` to the `Task.WhenAll` call.

**Step 6: Complete the SQL stub in section 6e**

Replace the comment-only stub in `EventHubOutbox.sql` section 6e with a complete query (in comment block like other queries):

```sql
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
```

**Step 7: Commit**

```bash
git add OutboxPublisher.cs EventHubOutbox.sql
git commit -m "feat: add orphan sweep loop to claim unowned partitions (C3)"
```

---

### Task 12: Add circuit breaker (C4)

**Files:**
- Modify: `OutboxPublisher.cs` (add circuit breaker state and logic)

**Step 1: Add circuit breaker options to OutboxPublisherOptions**

Add after `EventHubSendTimeoutSeconds`:

```csharp
/// <summary>
/// Consecutive send failures before the circuit opens for a topic.
/// While open, the publisher pauses polling to avoid burning retry counts.
/// Default: 3.
/// </summary>
public int CircuitBreakerFailureThreshold { get; set; } = 3;

/// <summary>
/// Seconds to keep the circuit open before attempting a half-open probe.
/// Default: 30.
/// </summary>
public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;
```

**Step 2: Add circuit breaker state fields**

Add to the State section:

```csharp
// Circuit breaker: tracks consecutive failures per topic.
private readonly Dictionary<string, int> _topicFailureCount = new();
private readonly Dictionary<string, DateTime> _topicCircuitOpenUntil = new();
private readonly object _circuitLock = new();
```

**Step 3: Add circuit breaker helper methods**

Add before the Helpers section:

```csharp
// -------------------------------------------------------------------------
// Circuit breaker
// -------------------------------------------------------------------------

private bool IsCircuitOpen(string topicName)
{
    lock (_circuitLock)
    {
        if (_topicCircuitOpenUntil.TryGetValue(topicName, out var openUntil))
        {
            if (DateTime.UtcNow < openUntil)
                return true;
            // Half-open: allow one attempt
            _topicCircuitOpenUntil.Remove(topicName);
        }
        return false;
    }
}

private void RecordSendSuccess(string topicName)
{
    lock (_circuitLock)
    {
        _topicFailureCount.Remove(topicName);
        _topicCircuitOpenUntil.Remove(topicName);
    }
}

private void RecordSendFailure(string topicName)
{
    lock (_circuitLock)
    {
        _topicFailureCount.TryGetValue(topicName, out int count);
        count++;
        _topicFailureCount[topicName] = count;

        if (count >= _options.CircuitBreakerFailureThreshold)
        {
            var openUntil = DateTime.UtcNow.AddSeconds(_options.CircuitBreakerOpenDurationSeconds);
            _topicCircuitOpenUntil[topicName] = openUntil;
            OnError($"Circuit breaker OPEN for topic '{topicName}' until {openUntil:O}", null);
        }
    }
}
```

**Step 4: Integrate circuit breaker into PublishBatchAsync**

In `PublishBatchAsync`, after getting `topicName` from the group key, add a circuit check:

```csharp
foreach (var group in byTopicAndKey)
{
    string topicName = group.Key.TopicName;
    string partitionKey = group.Key.PartitionKey;

    if (IsCircuitOpen(topicName))
    {
        OnError($"Circuit open for topic '{topicName}', skipping batch (rows will be retried after circuit closes)", null);
        continue;  // Don't lease or send — rows remain leased but will expire naturally
    }

    try
    {
        // ... existing send logic ...

        // After successful sends in the foreach over batches, record success:
        // (add after published.AddRange)
        RecordSendSuccess(topicName);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        OnError($"EventHub send timeout for topic '{topicName}' key '{partitionKey}'", null);
        RecordSendFailure(topicName);
    }
    catch (Exception ex)
    {
        OnError($"EventHub publish error for topic '{topicName}' key '{partitionKey}'", ex);
        RecordSendFailure(topicName);
    }
}
```

Note: when the circuit is open and we skip the batch, the leased rows will expire after `LeaseDurationSeconds`. The recovery path will pick them up, but since the circuit is still open, the publish loop will skip them again. Retry counts are NOT incremented because the rows are never re-leased by the recovery path (they're still leased by the primary path, just expired). Once the circuit closes, normal processing resumes.

**Step 5: Commit**

```bash
git add OutboxPublisher.cs
git commit -m "feat: add per-topic circuit breaker to pause during EventHub outages (C4)"
```

---

### Task 13: Populate lastError in dead-letter records (I6)

**Files:**
- Modify: `OutboxPublisher.cs` (capture and propagate error messages)

**Step 1: Add error tracking to PublishBatchAsync**

The cleanest approach: when a topic/key group fails, record the error so the dead-letter sweep can use it. Add a field:

```csharp
// Last known error per topic, for dead-letter diagnostics.
private volatile string? _lastPublishError;
```

In `PublishBatchAsync`, on the catch blocks, capture the error:

```csharp
catch (Exception ex)
{
    _lastPublishError = $"[{topicName}/{partitionKey}] {ex.GetType().Name}: {ex.Message}";
    OnError($"EventHub publish error for topic '{topicName}' key '{partitionKey}'", ex);
    RecordSendFailure(topicName);
}
```

**Step 2: Pass lastPublishError to the dead-letter sweep**

In `DeadLetterSweepLoopAsync`, change:

```csharp
await SweepDeadLettersAsync(lastError: _lastPublishError, ct).ConfigureAwait(false);
```

**Step 3: Commit**

```bash
git add OutboxPublisher.cs
git commit -m "fix: propagate last publish error to dead-letter records (I6)"
```

---

### Task 14: Final review pass

**Files:**
- Read: `OutboxPublisher.cs` (full file)
- Read: `EventHubOutbox.sql` (full file)
- Read: `EventHubOutboxSpec.md` (full file)

**Step 1: Read all three files and verify consistency**

Confirm:
- All C# SQL strings match their counterparts in `EventHubOutbox.sql`
- Spec text matches implementation behavior
- No orphaned code from the refactoring

**Step 2: Commit any final fixups**

```bash
git add -A
git commit -m "chore: final consistency pass after audit fixes"
```
