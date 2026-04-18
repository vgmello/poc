# Outbox Library — Known Limitations and Design Decisions

This document describes known limitations, transport-specific behaviors, and deliberate design trade-offs in the outbox library. These are not bugs — they are understood constraints that result from the at-least-once delivery guarantee and the architectural choices of each transport SDK.

---

## Transport-Specific Limitations

### Why Kafka has issues that EventHub doesn't

The Confluent.Kafka .NET SDK and the Azure EventHub SDK have fundamentally different APIs. Kafka's producer is callback-based with a synchronous `Flush`, while EventHub's producer is fully async (`SendAsync`). This difference produces three Kafka-specific issues that do not affect EventHub.

### Issue: Kafka `Flush` blocks a thread (mitigated)

**Affected:** `KafkaOutboxTransport.SendAsync`

The Confluent.Kafka `IProducer<K,V>.Flush()` method is synchronous by design — the library provides no async alternative. Each call blocks the calling thread for up to 100ms.

**Mitigation applied:** The flush loop now runs on a dedicated long-running thread via `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)`. This prevents ThreadPool starvation that could cause heartbeat timeouts, false staleness detection, and unnecessary partition rebalancing.

**Why EventHub doesn't have this:** EventHub uses `await _client.SendAsync(batch, ct)`, which is fully async and never blocks a ThreadPool thread. The Azure SDK was designed async-first.

**Residual risk:** Under sustained Kafka backpressure, the long-running thread is still blocked (by design), but this no longer affects the ThreadPool or the other publisher loops (heartbeat, rebalance, orphan sweep).

**Interaction with `PublishThreadCount`:** With the default `PublishThreadCount = 4`, up to 4 concurrent `SendAsync` calls can each trigger a long-running flush thread. This multiplies the thread usage but does not affect the ThreadPool since each uses `TaskCreationOptions.LongRunning`. If thread pressure is observed under high load, reduce `PublishThreadCount` for Kafka transports.

---

### Issue: Kafka ghost writes after flush timeout

**Affected:** `KafkaOutboxTransport.SendSubBatchAsync`

**What happens:** Messages are queued via `_producer.Produce()` (non-blocking, enqueues internally). Then `Flush` drains the internal queue. If the cancellation token fires (timeout) before all delivery reports arrive, `ThrowIfCancellationRequested()` throws `OperationCanceledException`. The method exits with an exception. But some messages may have already been delivered to Kafka — their delivery reports just hadn't arrived yet. The caller treats the entire batch as failed (increments the in-memory attempt counter), and those already-delivered messages will be re-sent on the next retry cycle.

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

**Impact if not resolved:**

- **Extra duplicates during timeout scenarios.** Since the outbox pattern already guarantees at-least-once (not exactly-once), duplicates are expected. This issue increases the duplicate rate during Kafka backpressure, but consumers must be idempotent regardless.
- **Ordering is preserved.** Ghost-written messages are re-fetched in `sequence_number` order by `FetchBatchAsync`, so the consumer sees duplicates but never out-of-order delivery within a partition key.
- **False dead-lettering under sustained backpressure.** Retry counts are in-memory and reset on publisher restart — failed messages get a fresh attempt budget on the next poll cycle. However, within a single poll cycle, each timeout against a given (topic, partitionKey) group counts as a non-transient failure and increments the in-memory attempt counter, even for messages that were already delivered. Under sustained Kafka backpressure within a single processing cycle, the in-memory attempt counter can reach `MaxPublishAttempts` and the messages get dead-lettered despite successful delivery. This does not cause data loss (the consumer already received them), but operators should be aware that dead-lettered messages from Kafka timeout scenarios may not actually be undelivered. Tuning `SendTimeoutSeconds` to match realistic Kafka latency reduces this risk.

---

## Design Decisions

### Background loops catch all exceptions (by design)

All four background loops (publish, heartbeat, rebalance, orphan sweep) have `catch (Exception ex)` handlers that log and continue. This means:

- **No single DB error can kill the publisher.** The service survives transient DB outages, network blips, and unexpected exceptions.
- **The heartbeat loop is the exception:** after 3 consecutive failures, it re-throws the exception instead of continuing. This is intentional — the heartbeat loop exiting triggers `RunLoopsWithRestartAsync`'s linked CTS to cancel all loops and restart them with exponential backoff. This is the primary mechanism for detecting persistent infrastructure failures.
- **The `RunLoopsWithRestartAsync` restart mechanism is a last resort.** It triggers when any loop task completes unexpectedly (exits its `while` loop). The heartbeat loop's re-throw after 3 failures is the main way this path is activated.
- **During a prolonged DB outage**, the service stays alive but non-functional. This is intentional: the publisher will automatically resume processing when the DB recovers without requiring a pod restart or manual intervention.

The trade-off: truly fatal errors (e.g., a corrupt state that causes infinite exceptions) will produce log spam rather than a clean crash. The `MaxConsecutiveRestarts` → `StopApplication()` escalation path handles this if loops repeatedly fail to stabilize.

### No UNIQUE constraint on dead_letter.sequence_number

The `outbox_dead_letter` table uses `dead_letter_seq` (auto-increment) as its primary key, not `sequence_number`. The `sequence_number` column is not unique because:

1. The same message can be dead-lettered, replayed, and dead-lettered again — each time getting a new `dead_letter_seq` but the same `sequence_number`.
2. The DELETE-then-INSERT pattern (atomic CTE in PostgreSQL, `DELETE...OUTPUT INTO` in SQL Server) makes accidental double-insertion nearly impossible through normal code paths.
3. A UNIQUE constraint would break the replay-then-dead-letter-again flow.

### Different hash algorithms across providers

PostgreSQL uses `hashtext(partition_key) & 2147483647` while SQL Server uses `ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT))`. These produce different hash values for the same input, meaning the same `partition_key` maps to different `partition_id` values across providers.

This is intentional: each provider uses its database's native hash function for performance (no UDF overhead). Cross-provider migration would require rehashing, but this is an unlikely scenario — outbox tables are operational (not archival) and are drained continuously.

### Options hot-reload support

Both the publisher service and store implementations use `IOptionsMonitor<OutboxPublisherOptions>`, meaning timing parameters like `HeartbeatTimeoutSeconds` and `PartitionGracePeriodSeconds` can be changed at runtime. Changes take effect on the next operation cycle.

**Snapshot-per-method guarantee:** Each store method snapshots `CurrentValue` once at the top of the method. All SQL steps within a single method (e.g., the 3-step rebalance transaction) use the same snapshot, preventing intra-operation inconsistency where different steps see different timeout values.

**Cross-instance caveat:** Changing these values at runtime requires care — if publisher instances pick up the new values at different times, they may briefly disagree on staleness thresholds. Prefer changing timing parameters via deployment (rolling restart) rather than hot-reload.

### No fail-fast validation on transport options

`KafkaTransportOptions.BootstrapServers` and `EventHubTransportOptions.ConnectionString` default to empty strings with no `IValidateOptions<T>` implementation. Misconfigured values are only discovered at the first publish attempt. This is because:

1. Transport options may come from external configuration (Key Vault, environment variables) that isn't available at DI registration time.
2. Connection validation requires network access, which is inappropriate during startup (it would block the host from starting).
3. The publisher's retry logic handles startup connectivity issues gracefully.

For fail-fast behavior, consumers of the library can add their own `IValidateOptions<KafkaTransportOptions>` implementation.

### Partition count changes require publisher downtime (both stores)

Changing the partition count (the modulus in `hash(partition_key) % total_partitions`) while publishers are running corrupts per-key message ordering. When the modulus changes, the same `partition_key` maps to a different `partition_id`, breaking the single-writer-per-partition guarantee. Two publishers can then process the same partition key simultaneously.

**SQL Server:** The modulus is baked into a persisted computed column (`PartitionId = ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % 64`). Changing it requires `ALTER TABLE` to drop and recreate the column, an index rebuild, and reseeding the partitions table. See `docs/production-runbook.md` for the procedure.

**PostgreSQL:** The modulus is computed at query time from the row count of `outbox_partitions`. Changing it requires only INSERT/DELETE on the partitions table (no schema change), but **all publishers must still be stopped** to prevent ordering corruption. The simpler schema change does not mean it's safe to do online.

This asymmetry is intentional: SQL Server's precomputed column enables Index Seek (7ms poll latency) instead of the full table scan (130ms) that runtime hash computation would require. PostgreSQL's query optimizer handles the runtime hash efficiently (3-5ms), so the precomputed column is not needed.

## EventHub integration tests

EventHub integration tests use Microsoft's Event Hubs emulator (`mcr.microsoft.com/azure-messaging/eventhubs-emulator:2.2.0`, multi-arch) plus Azurite as a sidecar, run via Testcontainers alongside Postgres, Redpanda, and SQL Server in the shared `InfrastructureFixture`.

The emulator caps at 10 event hubs per namespace. `EmulatorConfigGenerator` builds `Config.json` at fixture startup with 8 generic 4-partition hubs plus 2 named hubs for ordering/rebalance tests. Tests check out hub names via `EventHubTestHelper.CheckoutHub()` and use unique partition keys (`$"pk-{Guid.NewGuid():N}"`) for isolation since hubs are shared across concurrent tests.

Fault injection uses `EventHubFaultyTransportWrapper`, which wraps the real `EventHubOutboxTransport` and delegates successful sends to it. Unlike the Kafka wrapper (which bypasses `KafkaOutboxTransport`), the EventHub wrapper exercises the batch-split-retry-partial-send code path end-to-end.

The connection string uses `UseDevelopmentEmulator=true`, an Azure SDK sentinel that disables TLS and Entra auth. No real Azure namespace is required.

---

### SQL Server ghost records from continuous DELETE workload

**Affected:** SQL Server store only (PostgreSQL's VACUUM handles this natively)

The outbox pattern generates a continuous stream of DELETE operations as published messages are removed from the table. SQL Server does not immediately reclaim the physical space from deleted rows — instead it marks them as "ghost records" and relies on a background task (`ghost_cleanup`) to remove them asynchronously, typically within seconds.

Under normal load (< 1,000 msg/sec per publisher) the ghost cleanup task easily keeps pace and no operator action is needed. However, ghost records can accumulate faster than cleanup in two scenarios:

1. **Post-outage drain:** A prolonged broker outage (> 1 hour) causes millions of messages to accumulate in the outbox. When the broker recovers, the backlog drains rapidly — producing a burst of thousands of DELETEs per second that outpaces the ghost cleanup task.
2. **Sustained high throughput:** At 10K+ msg/sec across multiple publishers, continuous high-volume deletes can gradually outpace cleanup between cycles.

**Symptoms:** `IX_Outbox_Pending` bloats with ghost records and empty pages, causing `FetchBatchAsync` scans to slow down. Observable as increasing `outbox.poll.duration` p99 even when `outbox.messages.pending` is low.

**Mitigation:** Maintenance scripts are provided in `src/Outbox.SqlServer/db_scripts/maintenance/`. See the "SQL Server Index Maintenance" section in `docs/production-runbook.md` for scheduling guidance and procedures.
