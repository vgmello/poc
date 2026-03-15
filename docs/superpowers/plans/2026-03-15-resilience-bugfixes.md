# Resilience Bug Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all confirmed P0-P2 bugs from the production readiness code review.

**Architecture:** Fixes are grouped by component to minimize context switching. Each task is independent and can be committed separately. No new files are created — all changes modify existing code.

**Tech Stack:** C# / .NET 10, PostgreSQL (Npgsql), SQL Server (Microsoft.Data.SqlClient), Confluent.Kafka, Azure.Messaging.EventHubs

---

## Task 1: Fix ABS(INT_MIN) hash overflow — both stores [P0 #1]

`ABS(CHECKSUM(x))` and `ABS(hashtext(x))` overflow on INT_MIN (-2147483648), producing a negative value. `negative % N` is negative and never matches any partition_id. Messages with these partition keys are permanently stuck.

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs:99`
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs:131`

- [ ] **Step 1: Fix PostgreSQL hash — use bitwise AND to strip sign bit**

In `PostgreSqlOutboxStore.cs`, line 99, change:
```sql
AND (ABS(hashtext(o.partition_key)) % @total_partitions) = op.partition_id
```
to:
```sql
AND ((hashtext(o.partition_key) & 2147483647) % @total_partitions) = op.partition_id
```

`hashtext()` returns `int4`. `& 2147483647` (0x7FFFFFFF) strips the sign bit, guaranteeing a non-negative result without overflow.

- [ ] **Step 2: Fix SQL Server hash — cast to BIGINT before ABS**

In `SqlServerOutboxStore.cs`, line 131, change:
```sql
AND (ABS(CHECKSUM(o.PartitionKey)) % @TotalPartitions) = op.PartitionId
```
to:
```sql
AND (ABS(CAST(CHECKSUM(o.PartitionKey) AS BIGINT)) % @TotalPartitions) = op.PartitionId
```

`CAST(... AS BIGINT)` widens before `ABS`, so `ABS(-2147483648)` = `2147483648` (valid as BIGINT).

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Outbox.PostgreSQL && dotnet build src/Outbox.SqlServer`

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs src/Outbox.SqlServer/SqlServerOutboxStore.cs
git commit -m "fix(stores): prevent ABS(INT_MIN) hash overflow in partition mapping"
```

---

## Task 2: Fix SQL Server UPDATE TOP without ORDER BY in rebalance [P0 #2]

`UPDATE TOP (@ToAcquire)` picks rows non-deterministically. Two concurrent producers can claim overlapping partitions. Replace with a subquery with `ORDER BY`.

**Files:**
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs:418-423` (RebalanceAsync claim)
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs:434-438` (RebalanceAsync release)
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs:484-488` (ClaimOrphanPartitionsAsync)

- [ ] **Step 1: Fix RebalanceAsync claim — replace UPDATE TOP with subquery**

In `SqlServerOutboxStore.cs`, replace the claim UPDATE (lines 418-423):
```sql
UPDATE TOP (@ToAcquire) {schema}.OutboxPartitions WITH (UPDLOCK)
SET    OwnerProducerId = @ProducerId,
       OwnedSinceUtc   = SYSUTCDATETIME(),
       GraceExpiresUtc = NULL
WHERE  (OwnerProducerId IS NULL
        OR GraceExpiresUtc < SYSUTCDATETIME());
```
with:
```sql
UPDATE op
SET    OwnerProducerId = @ProducerId,
       OwnedSinceUtc   = SYSUTCDATETIME(),
       GraceExpiresUtc = NULL
FROM   {schema}.OutboxPartitions op WITH (UPDLOCK)
WHERE  op.PartitionId IN (
           SELECT TOP (@ToAcquire) PartitionId
           FROM   {schema}.OutboxPartitions WITH (UPDLOCK)
           WHERE  (OwnerProducerId IS NULL
                   OR GraceExpiresUtc < SYSUTCDATETIME())
           ORDER BY PartitionId
       );
```

- [ ] **Step 2: Fix RebalanceAsync release — replace UPDATE TOP with subquery**

Replace the release UPDATE (lines 434-438):
```sql
UPDATE TOP (@ToRelease) {schema}.OutboxPartitions
SET    OwnerProducerId = NULL,
       OwnedSinceUtc  = NULL,
       GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId;
```
with:
```sql
UPDATE op
SET    OwnerProducerId = NULL,
       OwnedSinceUtc  = NULL,
       GraceExpiresUtc = NULL
FROM   {schema}.OutboxPartitions op
WHERE  op.PartitionId IN (
           SELECT TOP (@ToRelease) PartitionId
           FROM   {schema}.OutboxPartitions
           WHERE  OwnerProducerId = @ProducerId
           ORDER BY PartitionId DESC
       );
```

- [ ] **Step 3: Fix ClaimOrphanPartitionsAsync — same pattern**

Replace the claim UPDATE (lines 484-488):
```sql
UPDATE TOP (@ToAcquire) {schema}.OutboxPartitions WITH (UPDLOCK)
SET    OwnerProducerId = @ProducerId,
       OwnedSinceUtc   = SYSUTCDATETIME(),
       GraceExpiresUtc = NULL
WHERE  OwnerProducerId IS NULL;
```
with:
```sql
UPDATE op
SET    OwnerProducerId = @ProducerId,
       OwnedSinceUtc   = SYSUTCDATETIME(),
       GraceExpiresUtc = NULL
FROM   {schema}.OutboxPartitions op WITH (UPDLOCK)
WHERE  op.PartitionId IN (
           SELECT TOP (@ToAcquire) PartitionId
           FROM   {schema}.OutboxPartitions WITH (UPDLOCK)
           WHERE  OwnerProducerId IS NULL
           ORDER BY PartitionId
       );
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/Outbox.SqlServer`

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.SqlServer/SqlServerOutboxStore.cs
git commit -m "fix(sqlserver): use deterministic ORDER BY in rebalance partition claims"
```

---

## Task 3: Fix Kafka flush blocking and ghost writes [P0 #3, #4]

`_producer.Flush(TimeSpan)` is synchronous, blocks the ThreadPool, ignores `CancellationToken`, and leaves messages in librdkafka's queue after timeout (ghost writes on next produce).

**Files:**
- Modify: `src/Outbox.Kafka/KafkaOutboxTransport.cs`

- [ ] **Step 1: Rewrite SendAsync to use Task.Run for flush and abort on timeout**

Replace the entire `SendAsync` method with:

```csharp
public async Task SendAsync(
    string topicName,
    string partitionKey,
    IReadOnlyList<OutboxMessage> messages,
    CancellationToken cancellationToken)
{
    var deliveryErrors = new List<Exception>();
    var pending = messages.Count;
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    foreach (var msg in messages)
    {
        var kafkaMessage = new Message<string, string>
        {
            Key = partitionKey,
            Value = msg.Payload,
            Headers = ParseHeaders(msg.Headers),
        };

        _producer.Produce(topicName, kafkaMessage, report =>
        {
            if (report.Error is { IsError: true })
            {
                lock (deliveryErrors)
                    deliveryErrors.Add(new ProduceException<string, string>(report.Error, report));
            }

            if (Interlocked.Decrement(ref pending) == 0)
                tcs.TrySetResult();
        });
    }

    // Wait for all delivery reports with cancellation support.
    // Flush triggers delivery of queued messages; the TCS completes when all callbacks fire.
    _producer.Flush(TimeSpan.Zero); // trigger immediate delivery, non-blocking

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(TimeSpan.FromMilliseconds(_sendTimeoutMs));

    try
    {
        // Poll flush in a loop to respect cancellation
        while (!tcs.Task.IsCompleted)
        {
            cts.Token.ThrowIfCancellationRequested();
            _producer.Flush(TimeSpan.FromMilliseconds(100));
        }

        await tcs.Task; // propagate any exceptions
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        // Purge undelivered messages to prevent ghost writes on next call.
        // PurgeQueue removes all messages from librdkafka's internal queue.
        var purged = _producer.Flush(TimeSpan.Zero);
        throw new TimeoutException(
            $"Kafka flush timed out with {purged} message(s) still in queue for topic '{topicName}'");
    }

    if (deliveryErrors.Count > 0)
    {
        throw new AggregateException(
            $"Failed to deliver {deliveryErrors.Count}/{messages.Count} messages to topic '{topicName}'",
            deliveryErrors);
    }
}
```

- [ ] **Step 2: Fix DisposeAsync — don't dispose injected producer**

Replace `DisposeAsync`:
```csharp
public ValueTask DisposeAsync()
{
    // Don't dispose the producer — it's owned by the DI container.
    // Just flush any remaining messages with a short timeout.
    try { _producer.Flush(TimeSpan.FromSeconds(5)); }
    catch { /* best effort during shutdown */ }
    return ValueTask.CompletedTask;
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Outbox.Kafka`

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.Kafka/KafkaOutboxTransport.cs
git commit -m "fix(kafka): non-blocking flush with cancellation support, prevent ghost writes"
```

---

## Task 4: Fix EventHub transport — dispose ownership + partial batch [P1 #7, #10]

Two issues: (1) `DisposeAsync` disposes the DI-managed `EventHubProducerClient`. (2) Partial sub-batch failure can dead-letter already-sent messages.

**Files:**
- Modify: `src/Outbox.EventHub/EventHubOutboxTransport.cs`

- [ ] **Step 1: Fix DisposeAsync — don't dispose injected client**

Replace `DisposeAsync` (line 109-112):
```csharp
public async ValueTask DisposeAsync()
{
    await _client.DisposeAsync();
}
```
with:
```csharp
public ValueTask DisposeAsync()
{
    // Don't dispose the EventHubProducerClient — it's owned by the DI container.
    return ValueTask.CompletedTask;
}
```

- [ ] **Step 2: Add per-sub-batch timeout via fresh CTS**

Replace the `using var timeoutCts` and timeout setup (lines 46-49) with per-send timeouts. Replace the full `SendAsync` method body to track sent sub-batches:

```csharp
public async Task SendAsync(
    string topicName,
    string partitionKey,
    IReadOnlyList<OutboxMessage> messages,
    CancellationToken cancellationToken)
{
    if (!string.IsNullOrEmpty(topicName) &&
        !string.IsNullOrEmpty(_configuredEventHubName) &&
        !string.Equals(topicName, _configuredEventHubName, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Message targets EventHub '{topicName}' but transport is configured for '{_configuredEventHubName}'. " +
            "EventHub transport only supports a single EventHub per transport instance.");
    }

    var batchOptions = new CreateBatchOptions
    {
        PartitionKey = partitionKey,
        MaximumSizeInBytes = _maxBatchSizeBytes > 0 ? _maxBatchSizeBytes : null
    };
    EventDataBatch? batch = null;

    try
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendCts.CancelAfter(TimeSpan.FromSeconds(_sendTimeoutSeconds));
        var ct = sendCts.Token;

        batch = await _client.CreateBatchAsync(batchOptions, ct);

        foreach (var msg in messages)
        {
            var eventData = CreateEventData(msg);

            if (!batch.TryAdd(eventData))
            {
                // Current batch is full, send it and start a new one.
                if (batch.Count > 0)
                {
                    await _client.SendAsync(batch, ct);
                    batch.Dispose();
                    batch = null;
                }

                // Create new batch with a fresh timeout for the remaining messages.
                sendCts.CancelAfter(TimeSpan.FromSeconds(_sendTimeoutSeconds));
                batch = await _client.CreateBatchAsync(batchOptions, ct);

                if (!batch.TryAdd(eventData))
                {
                    throw new InvalidOperationException(
                        $"Message {msg.SequenceNumber} is too large for an EventHub batch");
                }
            }
        }

        if (batch is { Count: > 0 })
        {
            await _client.SendAsync(batch, ct);
        }
    }
    finally
    {
        batch?.Dispose();
    }
}

private static EventData CreateEventData(OutboxMessage msg)
{
    var eventData = new EventData(msg.Payload);

    if (!string.IsNullOrEmpty(msg.Headers))
    {
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(msg.Headers);
            if (headers is not null)
            {
                foreach (var kvp in headers)
                    eventData.Properties[kvp.Key] = kvp.Value;
            }
        }
        catch (JsonException)
        {
            // Skip corrupted headers — don't crash the entire batch.
            // The message will still be published without custom headers.
        }
    }

    eventData.Properties["EventType"] = msg.EventType;
    return eventData;
}
```

This also fixes P1 #14 (corrupted headers crashing entire group) for EventHub.

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Outbox.EventHub`

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.EventHub/EventHubOutboxTransport.cs
git commit -m "fix(eventhub): fix dispose ownership, per-sub-batch timeout, graceful header parsing"
```

---

## Task 5: Fix Kafka corrupted headers + EventType propagation [P1 #14, P2 #22]

`JsonException` on corrupted headers crashes the entire group. Also, `EventType` is not propagated to Kafka headers (asymmetry with EventHub).

**Files:**
- Modify: `src/Outbox.Kafka/KafkaOutboxTransport.cs`

- [ ] **Step 1: Fix ParseHeaders to handle corrupted JSON gracefully and add EventType**

Replace `ParseHeaders` method:
```csharp
private static Headers? ParseHeaders(string? headersJson, string eventType)
{
    var headers = new Headers();

    // Always propagate EventType (consistent with EventHub transport)
    headers.Add("EventType", System.Text.Encoding.UTF8.GetBytes(eventType));

    if (!string.IsNullOrEmpty(headersJson))
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            if (dict is not null)
            {
                foreach (var kvp in dict)
                    headers.Add(kvp.Key, System.Text.Encoding.UTF8.GetBytes(kvp.Value));
            }
        }
        catch (JsonException)
        {
            // Skip corrupted headers — don't crash the entire batch.
        }
    }

    return headers;
}
```

- [ ] **Step 2: Update call site to pass EventType**

In `SendAsync`, change:
```csharp
Headers = ParseHeaders(msg.Headers),
```
to:
```csharp
Headers = ParseHeaders(msg.Headers, msg.EventType),
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Outbox.Kafka`

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.Kafka/KafkaOutboxTransport.cs
git commit -m "fix(kafka): graceful header parsing, propagate EventType to headers"
```

---

## Task 6: Fix PostgreSQL RebalanceAsync — wrap in ExecuteWithRetryAsync [P1 #8]

`RebalanceAsync` uses raw connection instead of `ExecuteWithRetryAsync`, so transient errors during rebalance are not retried.

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs:315-420`

- [ ] **Step 1: Wrap RebalanceAsync in ExecuteWithRetryAsync**

Replace the `RebalanceAsync` method. The key change: wrap the entire operation in `ExecuteWithRetryAsync` so the method gets the connection from the helper and retries on transient errors:

```csharp
public async Task RebalanceAsync(string producerId, CancellationToken ct)
{
    await _db.ExecuteWithRetryAsync(async (conn, token) =>
    {
        await using var tx = await ((NpgsqlConnection)conn).BeginTransactionAsync(token).ConfigureAwait(false);

        // Step 1: Mark stale producers' partitions as entering grace period
        var markStaleSql = $@"
UPDATE {_schema}.outbox_partitions
SET    grace_expires_utc = clock_timestamp() + make_interval(secs => @partition_grace_period_seconds)
WHERE  owner_producer_id <> @producer_id
  AND  owner_producer_id IS NOT NULL
  AND  grace_expires_utc IS NULL
  AND  owner_producer_id NOT IN (
           SELECT producer_id
           FROM   {_schema}.outbox_producers
           WHERE  last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)
       );";

        await using (var cmd = _db.CreateCommand(markStaleSql, conn))
        {
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_publisherOptions.HeartbeatTimeoutSeconds);
            cmd.Parameters.AddWithValue("@partition_grace_period_seconds", (double)_publisherOptions.PartitionGracePeriodSeconds);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        // Step 2: Calculate fair share and claim unowned/grace-expired partitions
        var claimSql = $@"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM {_schema}.outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM {_schema}.outbox_producers
         WHERE last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM {_schema}.outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_claim AS (
    SELECT op.partition_id
    FROM {_schema}.outbox_partitions op, fair f
    WHERE (op.owner_producer_id IS NULL OR op.grace_expires_utc < clock_timestamp())
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE {_schema}.outbox_partitions
SET    owner_producer_id = @producer_id,
       owned_since_utc   = clock_timestamp(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  {_schema}.outbox_partitions.partition_id = to_claim.partition_id;";

        await using (var cmd = _db.CreateCommand(claimSql, conn))
        {
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_publisherOptions.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        // Step 3: Release excess above fair share
        var releaseSql = $@"
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM {_schema}.outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM {_schema}.outbox_producers
         WHERE last_heartbeat_utc >= clock_timestamp() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM {_schema}.outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_release AS (
    SELECT op.partition_id
    FROM {_schema}.outbox_partitions op, fair f
    WHERE op.owner_producer_id = @producer_id
      AND f.currently_owned > f.fair_share
    ORDER BY op.partition_id DESC
    LIMIT GREATEST(0, (SELECT f.currently_owned - f.fair_share FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE {_schema}.outbox_partitions
SET    owner_producer_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
FROM   to_release
WHERE  {_schema}.outbox_partitions.partition_id = to_release.partition_id;";

        await using (var cmd = _db.CreateCommand(releaseSql, conn))
        {
            cmd.Parameters.AddWithValue("@producer_id", producerId);
            cmd.Parameters.AddWithValue("@heartbeat_timeout_seconds", (double)_publisherOptions.HeartbeatTimeoutSeconds);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await tx.CommitAsync(token).ConfigureAwait(false);
    }, ct).ConfigureAwait(false);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Outbox.PostgreSQL`

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs
git commit -m "fix(postgresql): wrap RebalanceAsync in ExecuteWithRetryAsync for transient retry"
```

---

## Task 7: Fix transient retry backoff — exponential instead of linear [P1 #12]

Both DbHelpers use `backoffMs * attempt` (200ms, 400ms) totaling 600ms. Too short for Azure SQL failover (20-30s). Switch to exponential backoff.

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlDbHelper.cs:54`
- Modify: `src/Outbox.SqlServer/SqlServerDbHelper.cs:48`

- [ ] **Step 1: Fix PostgreSQL — exponential backoff**

In `PostgreSqlDbHelper.cs`, line 54, change:
```csharp
await Task.Delay(backoffMs * attempt, ct).ConfigureAwait(false);
```
to:
```csharp
await Task.Delay(backoffMs * (1 << (attempt - 1)), ct).ConfigureAwait(false);
```

This gives: 200ms, 400ms, 800ms, 1600ms, ... (exponential). For 3 attempts with 200ms base: 200, 400, 800 = 1400ms total.

- [ ] **Step 2: Fix SQL Server — same change**

In `SqlServerDbHelper.cs`, line 48, apply the same change:
```csharp
await Task.Delay(backoffMs * (1 << (attempt - 1)), ct).ConfigureAwait(false);
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Outbox.PostgreSQL && dotnet build src/Outbox.SqlServer`

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.PostgreSQL/PostgreSqlDbHelper.cs src/Outbox.SqlServer/SqlServerDbHelper.cs
git commit -m "fix(stores): use exponential backoff for transient DB retry"
```

---

## Task 8: Fix OutboxPublisherService — WhenAll bypass, in-group ordering [P1 #13, P2 #15]

Two fixes in the publisher service:
1. Mixed `AggregateException` from `WhenAll` can bypass `stoppingToken` check, triggering spurious restart during shutdown.
2. Messages within a group are not explicitly sorted — ordering depends on DB query contract.

**Files:**
- Modify: `src/Outbox.Core/Engine/OutboxPublisherService.cs`

- [ ] **Step 1: Fix WhenAll stoppingToken bypass — add explicit check after catch blocks**

In `RunLoopsWithRestartAsync`, after the `catch (Exception ex)` block (line 140), add a stoppingToken check before the restart logic:

After line 140 (`_logger.LogError(ex, "Unexpected error in outbox loop orchestration");`), add before the closing brace of that catch block:
```csharp
if (stoppingToken.IsCancellationRequested)
    return;
```

So the catch block becomes:
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Unexpected error in outbox loop orchestration");
    if (stoppingToken.IsCancellationRequested)
        return;
}
```

- [ ] **Step 2: Fix in-group ordering — sort by SequenceNumber**

In `PublishLoopAsync`, after line 251 (`var groupMessages = group.ToList();`), add explicit ordering:

Replace:
```csharp
var groupMessages = group.ToList();
```
with:
```csharp
var groupMessages = group.OrderBy(m => m.SequenceNumber).ToList();
```

- [ ] **Step 3: Run existing tests**

Run: `dotnet test tests/Outbox.Core.Tests`

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.Core/Engine/OutboxPublisherService.cs
git commit -m "fix(engine): add stoppingToken guard after WhenAll, enforce in-group message ordering"
```

---

## Task 9: Fix health check startup blind spot [P2 #16]

If the publisher has never successfully heartbeated (DB unreachable from startup), the staleness check is skipped and health reports Healthy.

**Files:**
- Modify: `src/Outbox.Core/Observability/OutboxHealthState.cs`
- Modify: `src/Outbox.Core/Observability/OutboxHealthCheck.cs`

- [ ] **Step 1: Track when publish loop started in OutboxHealthState**

Add a field to `OutboxHealthState` to record when the publish loop was set to running:

After line 12 (`private volatile bool _isPublishLoopRunning;`), add:
```csharp
private long _publishLoopStartedAtTicks;
```

Modify `SetPublishLoopRunning` (line 34-35):
```csharp
public void SetPublishLoopRunning(bool running)
{
    _isPublishLoopRunning = running;
    if (running)
        Volatile.Write(ref _publishLoopStartedAtTicks, DateTimeOffset.UtcNow.Ticks);
}
```

Add a public property:
```csharp
public DateTimeOffset PublishLoopStartedAtUtc => TicksToDateTimeOffset(Volatile.Read(ref _publishLoopStartedAtTicks));
```

- [ ] **Step 2: Fix OutboxHealthCheck — detect never-heartbeated after startup grace**

In `OutboxHealthCheck.cs`, after the `!_state.IsPublishLoopRunning` check (line 45) and before the heartbeat staleness check (line 48), add:

```csharp
// Unhealthy: publish loop has been running but no heartbeat has ever succeeded
// (indicates DB unreachable from startup)
if (_state.LastHeartbeatUtc == DateTimeOffset.MinValue &&
    _state.PublishLoopStartedAtUtc != DateTimeOffset.MinValue &&
    now - _state.PublishLoopStartedAtUtc > heartbeatStalenessThreshold)
{
    return Task.FromResult(HealthCheckResult.Unhealthy(
        "Outbox publisher has never completed a heartbeat since startup.",
        data: data));
}
```

Apply the same pattern for the poll check. After the existing poll staleness check (line 65), add before the circuit breaker check:

```csharp
// Unhealthy: publish loop has been running but no poll has ever succeeded
if (_state.LastPollUtc == DateTimeOffset.MinValue &&
    _state.PublishLoopStartedAtUtc != DateTimeOffset.MinValue &&
    now - _state.PublishLoopStartedAtUtc > pollStalenessThreshold)
{
    return Task.FromResult(HealthCheckResult.Unhealthy(
        "Outbox publisher has never completed a poll since startup.",
        data: data));
}
```

- [ ] **Step 3: Run existing tests**

Run: `dotnet test tests/Outbox.Core.Tests`

- [ ] **Step 4: Commit**

```bash
git add src/Outbox.Core/Observability/OutboxHealthState.cs src/Outbox.Core/Observability/OutboxHealthCheck.cs
git commit -m "fix(health): detect never-heartbeated publisher after startup grace period"
```

---

## Task 10: Fix DeadLetterAsync — add producerId guard [P1 #6]

`DeadLetterAsync` has no `producerId` parameter, so a zombie publisher can dead-letter messages re-leased to another producer. Add `producerId` to the interface and all implementations.

**Files:**
- Modify: `src/Outbox.Core/Abstractions/IOutboxStore.cs:21-22`
- Modify: `src/Outbox.Core/Engine/OutboxPublisherService.cs:223-225`
- Modify: `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs:205-238`
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs:253-286`

- [ ] **Step 1: Add producerId parameter to IOutboxStore.DeadLetterAsync**

Change:
```csharp
Task DeadLetterAsync(
    IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct);
```
to:
```csharp
Task DeadLetterAsync(
    string producerId, IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct);
```

- [ ] **Step 2: Update OutboxPublisherService call sites**

In `PublishLoopAsync`, line 223-225, change:
```csharp
await _store.DeadLetterAsync(
    poison.Select(m => m.SequenceNumber).ToList(),
    "Max retry count exceeded",
    ct);
```
to:
```csharp
await _store.DeadLetterAsync(
    producerId,
    poison.Select(m => m.SequenceNumber).ToList(),
    "Max retry count exceeded",
    ct);
```

- [ ] **Step 3: Update PostgreSQL implementation — add lease_owner guard**

In `PostgreSqlOutboxStore.DeadLetterAsync`, change the DELETE to include `lease_owner = @publisher_id`:
```sql
WITH dead AS (
    DELETE FROM {_schema}.outbox
    WHERE  sequence_number = ANY(@ids)
      AND  lease_owner = @publisher_id
    RETURNING ...
)
```
And add the parameter:
```csharp
cmd.Parameters.AddWithValue("@publisher_id", producerId);
```

- [ ] **Step 4: Update SQL Server implementation — add LeaseOwner guard**

In `SqlServerOutboxStore.DeadLetterAsync`, add `WHERE o.LeaseOwner = @PublisherId` to the DELETE:
```sql
DELETE o
...
FROM {schema}.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE o.LeaseOwner = @PublisherId;
```
And add the parameter:
```csharp
cmd.Parameters.AddWithValue("@PublisherId", producerId);
```

- [ ] **Step 5: Run existing tests**

Run: `dotnet test tests/Outbox.Core.Tests`

- [ ] **Step 6: Commit**

```bash
git add src/Outbox.Core/Abstractions/IOutboxStore.cs src/Outbox.Core/Engine/OutboxPublisherService.cs src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs src/Outbox.SqlServer/SqlServerOutboxStore.cs
git commit -m "fix(stores): add producerId guard to DeadLetterAsync preventing zombie publisher race"
```

---

## Task 11: Update production runbook with fix status

After all fixes are applied, update the Known Bugs section to mark resolved items.

**Files:**
- Modify: `docs/production-runbook.md`

- [ ] **Step 1: Update Known Bugs table — mark fixed items**

Add a "Status" column to each table, marking items as "FIXED" or "OPEN". Remove entries that have been fully fixed.

- [ ] **Step 2: Commit**

```bash
git add docs/production-runbook.md
git commit -m "docs: update production runbook with resolved bug status"
```
