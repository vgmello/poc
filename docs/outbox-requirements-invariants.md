# Outbox Library — Requirements and Invariants

This document captures the critical invariants, behavioral requirements, and architectural constraints of the outbox library. Use this as a checklist when reviewing any implementation change.

---

## Core Guarantee

**At-least-once delivery.** Every message written to the outbox table MUST eventually be delivered to the broker OR moved to the dead letter table. No message may be silently lost. Duplicates at the broker are acceptable and expected — consumers MUST be idempotent.

---

## Invariants (must NEVER be violated)

### Message ordering

- **Within a (topic, partitionKey) group in a single batch:** messages MUST be sent to the broker in `sequence_number` order. Since `sequence_number` is `BIGINT IDENTITY` (SQL Server) / `bigserial` (PostgreSQL), this equals the order in which the rows were INSERTed.
- **Callers MUST insert messages in the order they want them delivered.** There is no caller-side override. `event_datetime_utc` is recorded for forensics but does NOT influence delivery order.
- **Across batches for the same partitionKey:** ordering is guaranteed by the SQL `ORDER BY sequence_number` in FetchBatch, combined with a version ceiling filter (`xmin < pg_snapshot_xmin(pg_current_snapshot())` on PostgreSQL, `RowVersion < MIN_ACTIVE_ROWVERSION()` on SQL Server) that withholds freshly inserted rows when earlier write transactions are still in-flight. This prevents the scenario where Transaction #2 commits before Transaction #1 and its rows are published out of order. Trade-off: any concurrent write transaction in the database temporarily pauses processing of new inserts until it commits.
- **Cross-partitionKey:** no ordering guarantee. This is by design.

### Attempt counter and dead-lettering

- Retry tracking is **in-memory only**, scoped to the current attempt of a single (topic, partitionKey) group. There is no persistent retry counter on outbox rows.
- The attempt counter MUST be incremented ONLY when a transport send fails with a **non-transient** error, as classified by `IOutboxTransport.IsTransient`.
- Transient errors (broker unreachable, timeouts, leader-election, etc.) MUST NOT consume the attempt counter. They MUST record a circuit breaker failure instead.
- Dead-lettering happens **inline** within the publish loop: when the attempt counter reaches `MaxPublishAttempts`, the failed messages are moved to the dead-letter table via `IOutboxStore.DeadLetterAsync` before the next group is processed.
- Dead-lettering MUST NOT happen while the circuit breaker is open. The retry loop checks `circuitBreaker.IsOpen(topic)` at the top of each iteration and exits via the `CircuitOpened` branch when the circuit is open.
- On publisher restart, in-memory attempt state is lost. Failed messages are re-fetched on the next poll and given a fresh attempt budget. This is acceptable under at-least-once semantics.

### Partition ownership

- Each partition MUST be owned by at most one active publisher at any time. This is the sole isolation mechanism preventing two publishers from processing the same message.
- Orphan partitions (owner is stale or NULL) MUST eventually be claimed via rebalance or orphan sweep.
- `FetchBatchAsync` performs a pure SELECT with no row locking. Isolation is entirely enforced by partition ownership — only the partition owner fetches and processes its messages.

### Partition count changes

- Changing the partition count (the modulus in `hash(partition_key) % total_partitions`) while publishers are running MUST NEVER happen. This applies to **both** SQL Server and PostgreSQL.
- When the modulus changes, the same `partition_key` maps to a different `partition_id`. This breaks the single-writer guarantee: publisher A may have in-flight messages for a key that now hashes to a partition owned by publisher B. Both publishers process the same partition key simultaneously, corrupting ordering.
- **SQL Server:** The modulus is baked into a persisted computed column (`PartitionId`). Changing it requires `ALTER TABLE` (inherently requires downtime).
- **PostgreSQL:** The modulus is computed at query time from `@total_partitions` (= row count of `outbox_partitions`). Adding rows to the partitions table changes the modulus dynamically, which is **equally dangerous**. All publishers MUST be stopped before changing the partition count.
- **Safe procedure (both stores):** Stop all publishers → drain outbox (or accept redelivery) → change partition count → reseed partition rows → restart publishers. See `docs/production-runbook.md` for detailed steps.

### Graceful shutdown

- On `StopAsync`, the publisher MUST call `UnregisterPublisherAsync` to release partitions. No lease release is required (there are no leases).
- The `finally` block in `ExecuteAsync` MUST use `CancellationToken.None` for cleanup operations.

### Startup resilience

- `RegisterPublisherAsync` MUST retry with exponential backoff. A transient DB failure at startup must NOT permanently kill the publisher.
- The publisher MUST be able to start and begin processing without any pre-existing data in the outbox table.

### Circuit breaker behavior

- When a circuit is **open**, the publisher MUST do nothing for the affected topic — messages stay in the outbox and will be picked up when the circuit closes. Broker outages must NOT advance the in-memory attempt counter.
- The circuit breaker MUST be fed by **transient** transport failures only. Non-transient failures (poison messages) MUST NOT trip the circuit, otherwise a single bad message could block the entire topic.
- After `CircuitBreakerOpenDurationSeconds`, the circuit transitions to **half-open** and allows one probe batch through.
- A single success in half-open MUST close the circuit. A failure MUST re-open it.

### Circuit breaker atomicity

- All circuit state transitions (Closed→Open, Open→HalfOpen, HalfOpen→Closed) MUST happen within the per-entry lock in `TopicCircuitBreaker`.
- Between any two concurrent operations on the same topic, there MUST be no intermediate observable state. `IsOpen()`, `RecordFailure()`, and `RecordSuccess()` are serialized per topic.

### Loop coordination via linked CancellationTokenSource

- All 4 loops (publish, heartbeat, rebalance, orphan sweep) share a single linked `CancellationTokenSource`.
- If ANY loop exits (normally or with exception), `linkedCts.CancelAsync()` MUST be called to signal all other loops to stop.
- This prevents orphaned loops that continue running after one loop crashes (e.g., heartbeat dies but publish loop keeps leasing).

### Event handler isolation

- Exceptions thrown by `IOutboxEventHandler` callbacks (`OnMessagePublishedAsync`, `OnPublishFailedAsync`, `OnCircuitBreakerStateChangedAsync`, `OnMessageDeadLetteredAsync`) MUST NOT trigger additional cleanup (dead-lettering, retry increment).
- By the time a handler is called, the message's fate (deleted, retried, dead-lettered) is already finalized.
- Each handler call MUST be wrapped in its own `try/catch` that logs and continues. Handler exceptions MUST NOT fall through to transport-failure catch blocks (which would incorrectly increment retry counts or record circuit breaker failures).
- Handler exceptions for poison messages (`OnMessageDeadLetteredAsync`) MUST NOT prevent healthy messages in the same batch from being processed.

### Health state consistency

- `_healthState.SetCircuitOpen/Closed` MUST be called BEFORE event handler callbacks (which can throw).
- `_healthState.SetPublishLoopRunning(false)` MUST be called in the `finally` block.
- `_healthState.SetPublishLoopRunning(true)` MUST reset all timestamp-based health indicators to 0, giving each loop restart a clean slate for health evaluation.
- `ConsecutiveLoopRestarts` MUST be reset after sustained healthy operation (30s threshold).

---

## Transport Contract (`IOutboxTransport.SendAsync`)

Any transport implementation MUST satisfy:

1. **Atomicity per call:** Either all messages in the call are sent, or an exception is thrown. Partial delivery is acceptable (at-least-once) but the exception MUST be thrown so the publisher knows to retry.
2. **Cancellation:** MUST respect the `CancellationToken`. On cancellation, in-flight messages may or may not be delivered (at-least-once handles this).
3. **Size awareness:** MUST handle messages that exceed broker limits gracefully (throw, don't crash). Single oversized messages should fail with a clear exception, not poison the entire batch.
4. **Sub-batching:** When messages exceed a single broker request limit, the transport SHOULD split into sub-batches internally (like EventHub's `TryAdd` pattern).
5. **No disposal of DI singletons:** `DisposeAsync` MUST NOT dispose injected dependencies (producer, client) — they're owned by the DI container.

### Header handling

- Headers are always `Dictionary<string, string>?`. The library serializes headers as JSON at the store boundary.
- Transports receive a dictionary directly — no parsing or deserialization needed.
- `PayloadContentType` is informational for the transport — payload bytes are always passed through to the broker without interpretation.
- System `EventType` header MUST always be added regardless of user-supplied headers, and MUST overwrite any user-supplied `EventType` in the dictionary.

### Kafka-specific requirements

- `Produce()` + `Flush()` pattern: messages are enqueued then flushed.
- Delivery report errors MUST be collected and thrown as `AggregateException`.
- `MaxBatchSizeBytes` MUST be respected — split into sub-batches before producing.
- The synchronous `Flush()` call blocks a thread. Callers should be aware.
- **`EnableIdempotence` MUST be `true` on the producer.** The library's partial-send retry path assumes delivery-report successes are always a contiguous prefix. Without idempotence, librdkafka can commit `seqN` after `seqN+1` under retries, producing a gapped success set; when the library then re-sends the failed subset, those messages land on the broker *after* later successes and per-partition-key ordering is corrupted. Idempotence is hard-wired in `KafkaOutboxBuilderExtensions` — there is no configuration knob to disable it.

### EventHub-specific requirements

- `CreateBatchAsync` + `TryAdd` + `SendAsync` pattern.
- Single messages too large for a batch MUST throw `InvalidOperationException`.
- `MaximumSizeInBytes` option MUST be passed to `CreateBatchOptions`.
- Topic name validation: EventHub supports only one hub per transport instance.

---

## Store Contract (`IOutboxStore`)

Any store implementation MUST satisfy:

1. **FetchBatch:** MUST return messages ordered by `sequence_number`, scoped per partition. MUST apply the version ceiling filter (`xmin < pg_snapshot_xmin(pg_current_snapshot())` on PostgreSQL, `RowVer < MIN_ACTIVE_ROWVERSION()` on SQL Server) to withhold rows from in-flight write transactions. MUST only return messages for partitions owned by the requesting publisher (with grace period check). MUST return `payload_content_type` alongside `headers`/`payload`. MUST deserialize `headers` from JSON text to `Dictionary<string, string>?`. This is a pure SELECT — no row locking or UPDATE is performed. **SQL Server: the query MUST use `WITH (NOLOCK)` on the outbox table.** Without it, `READ COMMITTED` takes shared locks during scanning and blocks on rows with exclusive locks from uncommitted transactions, causing timeouts. `NOLOCK` and `MIN_ACTIVE_ROWVERSION()` work together by design: `NOLOCK` prevents reader blocking on writer locks, while `MIN_ACTIVE_ROWVERSION()` filters out uncommitted rows by their row version. Do not remove `NOLOCK` — it is required for non-blocking reads.
2. **DeletePublished:** MUST delete the message unconditionally for the owning publisher's partition. Does NOT check `lease_owner` (column removed).
3. **DeadLetter:** MUST atomically delete from outbox and insert into dead_letter (CTE/OUTPUT INTO pattern). Does NOT check `lease_owner` (column removed). MUST carry `payload_content_type` through to the dead letter table. MUST write the supplied `attemptCount` parameter to the dead letter row's `AttemptCount` column.
4. **Heartbeat:** MUST update `last_heartbeat_utc` AND clear `grace_expires_utc` on owned partitions in the SAME transaction. If either update fails, both must roll back to prevent stale heartbeat with cleared grace periods (or vice versa).
5. **Rebalance:** MUST calculate fair share, mark stale publishers' partitions with grace period, claim available partitions, release excess. MUST run in a transaction.
6. **Transient retry:** All store operations MUST go through `ExecuteWithRetryAsync` with exponential backoff + jitter. MUST detect transient errors (deadlocks, timeouts, connection failures).
7. **Partition count caching:** The cached partition count MUST be refreshed at most once every 60 seconds. Reads and writes to the cache MUST use `Volatile.Read/Write` for cross-thread visibility without heavy locks.
8. **Content type propagation:** All operations that move messages between tables (dead-letter, replay) MUST include `payload_content_type` in both the source SELECT and destination INSERT column lists. The schema declares `payload_content_type` with `DEFAULT 'application/json'` as an ergonomic convenience for the common case — user-facing inserts that only emit JSON payloads can omit the column. This sits alongside the schema's other ergonomic defaults (`created_at_utc`, `registered_at_utc`, `last_heartbeat_utc`, `dead_lettered_at_utc`) and is not a legacy shim.

---

## Configuration Constraints

| Relationship                                                | Requirement                                              | Why                                          |
| ----------------------------------------------------------- | -------------------------------------------------------- | -------------------------------------------- |
| `HeartbeatIntervalMs * 3 <= HeartbeatTimeoutSeconds * 1000` | Must tolerate at least 2 missed heartbeats (3 intervals) | Prevents false staleness                     |
| `CircuitBreakerOpenDurationSeconds > 0`                     | Must eventually probe                                    | Prevents permanent circuit open              |
| `RetryBackoffMaxMs >= RetryBackoffBaseMs`                   | Max backoff must be at least the base                    | Prevents invalid backoff configuration       |
| `TransientRetryBackoffMs * 2^(MaxAttempts-1) > 20000`       | Retry budget must cover DB failover                      | Azure SQL failover takes 20-30s              |

---

## Anti-Patterns to Watch For

1. **Using `ct` (linked token) for cleanup operations.** Always use `CancellationToken.None` for `UnregisterPublisherAsync` and health state updates in failure paths.
2. **Tight loops without backoff.** Any loop that processes messages must have a delay when no progress is made (empty batch, all circuits open, all errors).
3. **Blocking ThreadPool threads.** Synchronous I/O on ThreadPool threads can starve other async operations (heartbeat, rebalance). Use `TaskCreationOptions.LongRunning` for blocking work.
4. **Catch-all without logging.** Every `catch (Exception)` must log. Silent swallowing hides production issues.
5. **DI singleton disposal in transport.** Transports must NOT dispose injected producers/clients in `DisposeAsync`.
6. **Catching generic Exception before PartialSendException.** `PartialSendException` MUST be caught before generic `Exception` in the publish loop. A generic handler that catches all exceptions would incorrectly dead-letter messages that were already delivered.
7. **Re-processing messages from event handler failures.** When `OnMessagePublishedAsync` or other handlers throw, do NOT retry or re-process the messages — their fate is already finalized. Each handler call must be individually try/caught to prevent fallthrough to transport-failure handlers.
8. **Letting poison message handler exceptions block healthy messages.** `OnMessageDeadLetteredAsync` runs before healthy message processing. If it throws unhandled, the entire healthy batch is skipped.
9. **Removing `WITH (NOLOCK)` from the SQL Server FetchBatch query.** `NOLOCK` is intentional and required — it prevents the reader from blocking on exclusive locks held by uncommitted write transactions. The `MIN_ACTIVE_ROWVERSION()` version ceiling filter provides the safety guarantee: it excludes any rows whose `RowVersion` is >= the earliest active transaction's row version, so uncommitted rows are never returned despite `NOLOCK`. Removing `NOLOCK` causes `READ COMMITTED` to block on in-flight inserts, leading to query timeouts. Note: `NOLOCK` is only safe here because the outbox table is append-only (INSERT + DELETE, no UPDATEs to payload columns).
10. **Burning the attempt counter on transient errors.** The `IOutboxTransport.IsTransient` classifier exists so transient outages don't consume the retry budget. A change that increments the attempt counter on a transient catch reintroduces the bug where outages dead-letter healthy messages.
11. **Recording a circuit breaker failure on non-transient errors.** Non-transient (message-poison) failures must not trip the circuit. The whole point of the clean separation is that one bad message can't block other groups for the same topic.
12. **Dead-lettering while the circuit is open.** The retry loop must check `IsOpen(topic)` at the top of every attempt and exit via `CircuitOpened`, not via the DLQ branch. DLQing during an outage produces out-of-order DLQ inserts and confuses operators.
13. **Reintroducing a long-lived retry-count dictionary.** The attempt counter is a local variable on the worker thread's stack, scoped to one group's processing. A change that promotes it to a `ConcurrentDictionary` keyed by sequence number reintroduces the cross-batch state model this design rejects.
