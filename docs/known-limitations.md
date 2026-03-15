# Outbox Library — Known Limitations and Design Decisions

This document describes known limitations, transport-specific behaviors, and deliberate design trade-offs in the outbox library. These are not bugs — they are understood constraints that result from the at-least-once delivery guarantee and the architectural choices of each transport SDK.

---

## Transport-Specific Limitations

### Why Kafka has issues that EventHub doesn't

The Confluent.Kafka .NET SDK and the Azure EventHub SDK have fundamentally different APIs. Kafka's producer is callback-based with a synchronous `Flush`, while EventHub's producer is fully async (`SendAsync`). This difference produces three Kafka-specific issues that do not affect EventHub.

### Issue: Kafka `Flush` blocks ThreadPool threads

**Affected:** `KafkaOutboxTransport.SendAsync` (line 66-70)

```csharp
while (!tcs.Task.IsCompleted)
{
    cts.Token.ThrowIfCancellationRequested();
    _producer.Flush(TimeSpan.FromMilliseconds(100)); // synchronous, blocking
}
```

**What happens:** The Confluent.Kafka `IProducer<K,V>.Flush()` method is synchronous by design — the library provides no async alternative. Each call blocks the calling ThreadPool thread for up to 100ms. Under Kafka backpressure (broker slow, network congestion), this ties up a ThreadPool thread for the entire send timeout (default 15s). Since the outbox publisher's other loops (heartbeat, rebalance, orphan sweep) also run on the ThreadPool, thread starvation under load could cause heartbeat timeouts, false staleness detection, and unnecessary partition rebalancing.

**Why EventHub doesn't have this:** EventHub uses `await _client.SendAsync(batch, ct)`, which is fully async and never blocks a ThreadPool thread. The Azure SDK was designed async-first.

**How to resolve:** Replace the synchronous flush loop with a dedicated long-running thread (not a ThreadPool thread) that handles flushing:

```csharp
// Option A: Use Task.Run with TaskCreationOptions.LongRunning
var flushTask = Task.Factory.StartNew(() =>
{
    while (!tcs.Task.IsCompleted)
    {
        cts.Token.ThrowIfCancellationRequested();
        _producer.Flush(TimeSpan.FromMilliseconds(100));
    }
}, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
await flushTask;

// Option B: Use ProduceAsync instead of Produce+Flush (simpler but
// loses batching optimization — ProduceAsync flushes after each message)
foreach (var msg in messages)
    await _producer.ProduceAsync(topicName, kafkaMessage, ct);
```

Option A preserves the batching behavior while keeping the ThreadPool free. Option B is simpler but trades throughput for simplicity. For most workloads, Option B is sufficient.

**Impact if not resolved:** Under sustained Kafka backpressure, ThreadPool starvation can cause heartbeat timeouts (false producer staleness) leading to unnecessary partition rebalancing. In practice, this only manifests under very high load with a slow broker.

---

### Issue: Kafka ghost writes after flush timeout

**Affected:** `KafkaOutboxTransport.SendAsync` (line 66-72)

**What happens:** Messages are queued via `_producer.Produce()` (non-blocking, enqueues internally). Then `Flush` drains the internal queue. If the cancellation token fires (timeout) before all delivery reports arrive, `ThrowIfCancellationRequested()` throws `OperationCanceledException`. The method exits with an exception. But some messages may have already been delivered to Kafka — their delivery reports just hadn't arrived yet. The caller treats the entire batch as failed (`ReleaseLeaseAsync(incrementRetry: true)`), and those already-delivered messages will be re-sent on the next retry cycle.

**Why EventHub doesn't have this:** EventHub's `_client.SendAsync(batch, ct)` is atomic per batch. Either the entire batch is accepted or it isn't. There is no intermediate state where "some messages are delivered but the SDK doesn't know yet." The Azure SDK handles cancellation internally and provides a clean success/failure boundary.

**How to resolve:** Wait for all delivery reports before checking cancellation. Instead of checking cancellation in the flush loop, await the `TaskCompletionSource` with the timeout:

```csharp
// Replace the flush loop + await with:
_producer.Flush(TimeSpan.FromMilliseconds(_sendTimeoutMs));

// Or, use a proper async wait on the TCS:
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeoutCts.CancelAfter(_sendTimeoutMs);

// Flush on a background thread to not block
_ = Task.Factory.StartNew(() => _producer.Flush(TimeSpan.FromMilliseconds(_sendTimeoutMs)),
    TaskCreationOptions.LongRunning);

try
{
    await tcs.Task.WaitAsync(timeoutCts.Token);
}
catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
{
    // Timeout, not shutdown. Some messages may already be delivered.
    // Log but don't increment retry for those.
    throw new TimeoutException($"Kafka flush timed out after {_sendTimeoutMs}ms");
}
```

The key insight: after a timeout, we should NOT assume all messages failed. The safer approach is to wait for all delivery reports (even if it takes slightly longer than the timeout) before deciding which messages succeeded and which failed. This eliminates ghost writes at the cost of slightly longer timeout behavior.

**Impact if not resolved:** Extra duplicates during timeout scenarios. Since the outbox pattern already guarantees at-least-once (not exactly-once), duplicates are expected. This issue increases the duplicate rate during Kafka backpressure, but consumers must be idempotent regardless.

---

### Issue: Partial batch failure causes unbounded duplication (Kafka and EventHub)

**Affected:** Both transports, but manifests differently.

**Kafka:** `KafkaOutboxTransport.SendAsync` — all messages in a group are produced atomically via `Produce()` + `Flush()`. If some delivery reports succeed and others fail, the method throws. The caller releases ALL messages with retry increment. Successfully-delivered messages will be re-sent.

**EventHub:** `EventHubOutboxTransport.SendAsync` (line 65-89) — when messages don't fit in one `EventDataBatch`, multiple sub-batches are sent sequentially. If sub-batch 1 succeeds and sub-batch 2 fails, the exception propagates. The caller releases ALL messages. Sub-batch 1's messages are already on EventHub and will be re-sent.

**What happens in practice:** Under repeated partial failures, the first sub-batch's messages accumulate duplicates with each retry cycle. With N retries before dead-lettering, the first sub-batch's messages could be delivered up to N times.

**Why this is hard to fix:** The fundamental issue is that `IOutboxTransport.SendAsync` is all-or-nothing from the publisher's perspective, but the transport may have partially succeeded internally. Fixing this properly requires per-message tracking:

```csharp
// Hypothetical fix: return which messages succeeded
public record SendResult(
    IReadOnlyList<long> SucceededSequenceNumbers,
    IReadOnlyList<long> FailedSequenceNumbers,
    Exception? Error);

// Then the publisher can:
// - Delete succeeded messages
// - Release failed messages with retry increment
// - Not release succeeded messages
```

This would require changing the `IOutboxTransport` interface, which is a breaking change. It also adds complexity to every transport implementation.

**Impact if not resolved:** Higher duplicate rate during partial failures. Consumers must be idempotent regardless of this issue (at-least-once guarantee). The duplicate rate is bounded by `MaxRetryCount` — after that, messages are dead-lettered.

---

### Issue: Sweep can dead-letter already-published messages (race condition)

**Affected:** `SweepDeadLettersAsync` in both PostgreSQL and SQL Server stores.

**What happens:** A race condition exists between `SendAsync` + `DeletePublishedAsync` and `SweepDeadLettersAsync`:

1. Publisher sends messages to broker (success)
2. Publisher's lease expires (took too long, or publisher paused by GC)
3. `SweepDeadLettersAsync` runs, finds messages with `retry_count >= MaxRetryCount` and `leased_until_utc < now`
4. Sweep moves messages to dead_letter table
5. Publisher tries `DeletePublishedAsync` — rows are gone (no error, 0 rows affected)

Result: messages are both in the broker AND in the dead_letter table.

**Why this is rare:** The window is narrow: the publisher must succeed at sending, then not delete within `LeaseDurationSeconds` (default 45s). This typically only happens under extreme GC pressure or if `DeletePublishedAsync` fails repeatedly (which our P1 fix now handles without burning retries).

**Mitigation:** The sweep uses `FOR UPDATE SKIP LOCKED` (PostgreSQL) or `READPAST` (SQL Server), which skips rows locked by in-flight transactions. If `DeletePublishedAsync` is actively running, the sweep skips those rows. The race only occurs if the delete hasn't started yet.

**If it happens:** The dead-lettered messages can be safely purged via `IDeadLetterManager.PurgeAsync` since they're already published. Monitor for messages that appear in both the broker and dead_letter table.

---

## Design Decisions

### Background loops catch all exceptions (by design)

All five background loops (publish, heartbeat, rebalance, orphan sweep, dead-letter sweep) have `catch (Exception ex)` handlers that log and continue. This means:

- **No single DB error can kill the publisher.** The service survives transient DB outages, network blips, and unexpected exceptions.
- **The `RunLoopsWithRestartAsync` restart mechanism is a last resort.** It only triggers if a loop task completes unexpectedly (returns from its `while` loop), which doesn't happen through normal exception paths.
- **During a prolonged DB outage**, the service stays alive but non-functional. This is intentional: the publisher will automatically resume processing when the DB recovers without requiring a pod restart or manual intervention.

The trade-off: truly fatal errors (e.g., a corrupt state that causes infinite exceptions) will produce log spam rather than a clean crash. The `MaxConsecutiveRestarts` → `StopApplication()` escalation path handles this if a loop does manage to exit.

### No UNIQUE constraint on dead_letter.sequence_number

The `outbox_dead_letter` table uses `dead_letter_seq` (auto-increment) as its primary key, not `sequence_number`. The `sequence_number` column is not unique because:

1. The same message can be dead-lettered, replayed, and dead-lettered again — each time getting a new `dead_letter_seq` but the same `sequence_number`.
2. The DELETE-then-INSERT pattern (atomic CTE in PostgreSQL, `DELETE...OUTPUT INTO` in SQL Server) makes accidental double-insertion nearly impossible through normal code paths.
3. A UNIQUE constraint would break the replay-then-dead-letter-again flow.

### Different hash algorithms across providers

PostgreSQL uses `hashtext(partition_key) & 2147483647` while SQL Server uses `ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT))`. These produce different hash values for the same input, meaning the same `partition_key` maps to different `partition_id` values across providers.

This is intentional: each provider uses its database's native hash function for performance (no UDF overhead). Cross-provider migration would require rehashing, but this is an unlikely scenario — outbox tables are operational (not archival) and are drained continuously.

### EventHub timeout shared across sub-batches

When EventHub messages span multiple sub-batches, a single `CancellationTokenSource` with `CancelAfter` governs the entire send. Later sub-batches get less remaining time. The `CancelAfter` is reset between sub-batches (line 76: `sendCts.CancelAfter(...)`), but if the first batch took most of the budget, the reset starts from "now" which is correct.

### No fail-fast validation on transport options

`KafkaTransportOptions.BootstrapServers` and `EventHubTransportOptions.ConnectionString` default to empty strings with no `IValidateOptions<T>` implementation. Misconfigured values are only discovered at the first publish attempt. This is because:

1. Transport options may come from external configuration (Key Vault, environment variables) that isn't available at DI registration time.
2. Connection validation requires network access, which is inappropriate during startup (it would block the host from starting).
3. The publisher's retry logic handles startup connectivity issues gracefully.

For fail-fast behavior, consumers of the library can add their own `IValidateOptions<KafkaTransportOptions>` implementation.
