# Outbox Library — Requirements and Invariants

This document captures the critical invariants, behavioral requirements, and architectural constraints of the outbox library. Use this as a checklist when reviewing any implementation change.

---

## Core Guarantee

**At-least-once delivery.** Every message written to the outbox table MUST eventually be delivered to the broker OR moved to the dead letter table. No message may be silently lost. Duplicates at the broker are acceptable and expected — consumers MUST be idempotent.

---

## Invariants (must NEVER be violated)

### Message ordering

- **Within a (topic, partitionKey) group in a single batch:** messages MUST be sent to the broker in `SequenceNumber` order.
- **Across batches for the same partitionKey:** ordering is guaranteed by the SQL `ORDER BY event_datetime_utc, event_ordinal` in LeaseBatch. Earlier messages are leased before later ones.
- **Cross-partitionKey:** no ordering guarantee. This is by design.

### Retry count accuracy

- `retry_count` MUST be incremented ONLY when a **transport send fails** (explicit failure).
- `retry_count` MUST be incremented when a **lease expires** (implicit failure — previous holder crashed).
- `retry_count` MUST NOT be incremented when: circuit breaker is open (release without increment), or transport succeeded but delete failed.
- Once `retry_count >= MaxRetryCount`, the message MUST be moved to the dead letter table.

### Lease ownership

- `DeletePublishedAsync` and `ReleaseLeaseAsync` MUST check `lease_owner = @publisher_id`. A publisher must never delete/release another publisher's leased messages.
- `DeadLetterAsync` MUST check `lease_owner = @publisher_id` for the same reason.
- `LeaseBatchAsync` MUST use `FOR UPDATE SKIP LOCKED` (PostgreSQL) or `WITH (ROWLOCK, READPAST)` (SQL Server) to prevent two publishers from leasing the same message.

### Partition ownership

- Each partition MUST be owned by at most one active publisher at any time.
- The grace period (`PartitionGracePeriodSeconds`) MUST be longer than `LeaseDurationSeconds` to prevent two publishers from processing the same partition simultaneously.
- Orphan partitions (owner is stale or NULL) MUST eventually be claimed via rebalance or orphan sweep.

### Graceful shutdown

- On `StopAsync`, the publisher MUST release all in-flight leases (`incrementRetry: false`).
- On `StopAsync`, the publisher MUST call `UnregisterPublisherAsync` to release partitions.
- The `finally` block in `ExecuteAsync` MUST use `CancellationToken.None` for cleanup operations.

### Startup resilience

- `RegisterPublisherAsync` MUST retry with exponential backoff. A transient DB failure at startup must NOT permanently kill the publisher.
- The publisher MUST be able to start and begin processing without any pre-existing data in the outbox table.

### Circuit breaker behavior

- When a circuit is **open**, messages MUST be released with `incrementRetry: false`. Broker outages must NOT burn retry counts.
- After `CircuitBreakerOpenDurationSeconds`, the circuit transitions to **half-open** and allows one probe batch through.
- A single success in half-open MUST close the circuit. A failure MUST re-open it.

### Unprocessed sequences safety net (BUG-5)

- At the start of each publish iteration, ALL leased message sequence numbers MUST be added to an `unprocessedSequences` tracking set.
- Sequences are removed from the set ONLY after their fate is finalized (deleted, released, or dead-lettered).
- The `finally` block MUST release any sequences still in the set with `incrementRetry: false` and `CancellationToken.None`.
- This prevents message loss on SIGTERM or loop cancellation — messages leased but not yet processed are immediately released for another publisher.

### Circuit breaker atomicity

- All circuit state transitions (Closed→Open, Open→HalfOpen, HalfOpen→Closed) MUST happen within the per-entry lock in `TopicCircuitBreaker`.
- Between any two concurrent operations on the same topic, there MUST be no intermediate observable state. `IsOpen()`, `RecordFailure()`, and `RecordSuccess()` are serialized per topic.

### Loop coordination via linked CancellationTokenSource

- All 5 loops (publish, heartbeat, rebalance, orphan sweep, dead-letter sweep) share a single linked `CancellationTokenSource`.
- If ANY loop exits (normally or with exception), `linkedCts.CancelAsync()` MUST be called to signal all other loops to stop.
- This prevents orphaned loops that continue running after one loop crashes (e.g., heartbeat dies but publish loop keeps leasing).

### Event handler isolation

- Exceptions thrown by `IOutboxEventHandler` callbacks (`OnMessagePublishedAsync`, `OnPublishFailedAsync`, `OnCircuitBreakerStateChangedAsync`, `OnMessageDeadLetteredAsync`) MUST NOT trigger additional cleanup (lease release, dead-lettering, retry increment).
- By the time a handler is called, the message's fate (deleted, released, dead-lettered) is already finalized.
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

### EventHub-specific requirements

- `CreateBatchAsync` + `TryAdd` + `SendAsync` pattern.
- Single messages too large for a batch MUST throw `InvalidOperationException`.
- `MaximumSizeInBytes` option MUST be passed to `CreateBatchOptions`.
- Topic name validation: EventHub supports only one hub per transport instance.

---

## Store Contract (`IOutboxStore`)

Any store implementation MUST satisfy:

1. **LeaseBatch:** MUST return messages ordered by `event_datetime_utc, event_ordinal`. MUST use row-level locking with skip-locked semantics. MUST only return messages for partitions owned by the requesting publisher (with grace period check). MUST return `payload_content_type` alongside `headers`/`payload`. MUST deserialize `headers` from JSON text to `Dictionary<string, string>?`.
2. **DeletePublished:** MUST check `lease_owner`. MUST NOT delete messages owned by another publisher.
3. **ReleaseLease:** MUST check `lease_owner`. `incrementRetry: true` MUST increment `retry_count`. `incrementRetry: false` MUST NOT.
4. **DeadLetter:** MUST atomically delete from outbox and insert into dead_letter (CTE/OUTPUT INTO pattern). MUST check `lease_owner`. MUST carry `payload_content_type` through to the dead letter table.
5. **Heartbeat:** MUST update `last_heartbeat_utc` AND clear `grace_expires_utc` on owned partitions in the SAME transaction. If either update fails, both must roll back to prevent stale heartbeat with cleared grace periods (or vice versa).
6. **Rebalance:** MUST calculate fair share, mark stale publishers' partitions with grace period, claim available partitions, release excess. MUST run in a transaction.
7. **Transient retry:** All store operations MUST go through `ExecuteWithRetryAsync` with exponential backoff + jitter. MUST detect transient errors (deadlocks, timeouts, connection failures).
8. **Partition count caching:** The cached partition count MUST be refreshed at most once every 60 seconds. Reads and writes to the cache MUST use `Volatile.Read/Write` for cross-thread visibility without heavy locks.
9. **Dead-letter sweep conditions:** `SweepDeadLettersAsync` MUST only dead-letter messages when: (a) `lease_owner IS NULL` (explicitly released), OR (b) lease expired beyond `LeaseDurationSeconds` (stale), OR (c) lease owner is not in the active publishers table (dead publisher). Messages actively leased by a live publisher MUST NOT be swept, even if `retry_count >= MaxRetryCount`.
10. **Content type propagation:** All operations that move messages between tables (dead-letter, sweep, replay) MUST include `payload_content_type` in both the source SELECT and destination INSERT column lists. Content types default to `'application/json'` in the schema for backward compatibility with inserts that omit them.

---

## Configuration Constraints

| Relationship                                                | Requirement                                              | Why                                              |
| ----------------------------------------------------------- | -------------------------------------------------------- | ------------------------------------------------ |
| `HeartbeatIntervalMs * 3 <= HeartbeatTimeoutSeconds * 1000` | Must tolerate at least 2 missed heartbeats (3 intervals) | Prevents false staleness                         |
| `LeaseDurationSeconds < PartitionGracePeriodSeconds`        | Grace period must outlast leases                         | Prevents two publishers processing same partition |
| `CircuitBreakerOpenDurationSeconds > 0`                     | Must eventually probe                                    | Prevents permanent circuit open                  |
| `MaxRetryCount > CircuitBreakerFailureThreshold`            | Retries must outlast circuit threshold                   | Prevents dead-lettering before circuit opens     |
| `TransientRetryBackoffMs * 2^(MaxAttempts-1) > 20000`       | Retry budget must cover DB failover                      | Azure SQL failover takes 20-30s                  |

---

## Anti-Patterns to Watch For

1. **Using `ct` (linked token) for cleanup operations.** Always use `CancellationToken.None` for `ReleaseLeaseAsync`, `UnregisterPublisherAsync`, and health state updates in failure paths.
2. **Incrementing retry count after transport success.** If `SendAsync` succeeded, any subsequent failure (delete, event handler) must NOT increment retry count.
3. **Tight loops without backoff.** Any loop that processes messages must have a delay when no progress is made (empty batch, all circuits open, all errors).
4. **Blocking ThreadPool threads.** Synchronous I/O on ThreadPool threads can starve other async operations (heartbeat, rebalance). Use `TaskCreationOptions.LongRunning` for blocking work.
5. **Catch-all without logging.** Every `catch (Exception)` must log. Silent swallowing hides production issues.
6. **DI singleton disposal in transport.** Transports must NOT dispose injected producers/clients in `DisposeAsync`.
7. **Catching generic Exception before PartialSendException.** `PartialSendException` MUST be caught before generic `Exception` in the publish loop. A generic handler would release ALL messages with `incrementRetry: true`, incorrectly penalizing messages that were already delivered.
8. **Re-releasing messages from event handler failures.** When `OnMessagePublishedAsync` or other handlers throw, do NOT re-release or retry the messages — their fate is already finalized. Each handler call must be individually try/caught to prevent fallthrough to transport-failure handlers.
9. **Letting poison message handler exceptions block healthy messages.** `OnMessageDeadLetteredAsync` runs before healthy message processing. If it throws unhandled, the entire healthy batch is skipped and stuck until lease expiry with spurious retry count increment.
