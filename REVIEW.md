# Production Readiness Review — Outbox Publisher Library

## BUGS (Correctness Issues)

### 1. No delay after outer-loop errors — tight spin on persistent failures (P0)

**File:** `OutboxPublisherService.cs:157-161`

When `LeaseBatchAsync` or any outer operation throws, the `catch` logs the error but immediately loops back with zero delay. If the database is down, this creates a CPU-burning tight loop hammering the DB with connection attempts.

**Fix:** Add a delay (e.g., `MaxPollIntervalMs`) after the outer catch before continuing the loop.

---

### 2. `RecordSuccess` falsely reports state changes (P2)

**File:** `TopicCircuitBreaker.cs:79-88`

`RecordSuccess` returns `stateChanged=true` when transitioning from `Closed` (with `failureCount > 0`) back to `Closed`. The state never actually changed — only the counter reset. This fires spurious `OnCircuitBreakerStateChangedAsync` callbacks and inflates the `CircuitBreakerStateChanges` metric.

**Fix:** Only report `stateChanged=true` when `previousState != CircuitState.Closed`.

---

### 3. EventHub transport leaks `EventDataBatch` on exception (P0)

**File:** `EventHubOutboxTransport.cs:27-83`

If `_client.SendAsync(batch, ct)` throws at line 62 or 79, the `batch` object is never disposed. The `batch.Dispose()` at line 82 is only reached on the happy path. Should use `try/finally` or make `batch` a `using` variable that's reassigned.

**Fix:** Wrap the batch lifecycle in `try/finally` to guarantee disposal on all paths.

---

### 4. Kafka transport sends one-by-one — partial sends cause extra duplicates (P1)

**File:** `KafkaOutboxTransport.cs:32-46`

Messages are sent individually via `ProduceAsync`. If message 5/10 fails, messages 1-4 are already committed to Kafka, but the engine releases the lease for all 10. On re-lease, messages 1-4 are sent again.

The outbox pattern is at-least-once by design, but this unnecessarily amplifies duplicates. Consider using the `Produce` (non-async) + `Flush` pattern for batch-atomic delivery.

---

### 5. EventHub transport ignores `topicName` (P1)

**File:** `EventHubOutboxTransport.cs:27`

The `topicName` parameter is ignored. All messages go to whichever EventHub was configured at construction time. If messages target different event hubs, they silently go to the wrong destination.

**Fix:** Either validate that `topicName` matches the configured EventHub, or support multiple EventHub clients.

---

### 6. `UnregisterProducerAsync` has no retry logic — both providers (P1)

**Files:** `SqlServerOutboxStore.cs:68-107`, `PostgreSqlOutboxStore.cs:57-82`

Unlike every other method, unregister opens a raw connection without `ExecuteWithRetryAsync`. A transient error during shutdown leaves orphaned producer registrations and partition ownership that won't be cleaned up until heartbeat timeout expires.

---

## RELIABILITY / RESILIENCE CONCERNS

### 7. `GetTotalPartitionsAsync` called on every poll — unnecessary DB round-trip (P2)

**Files:** `SqlServerOutboxStore.cs:121`, `PostgreSqlOutboxStore.cs:118`

Every `LeaseBatchAsync` call makes an extra query to count partitions. The partition count is effectively static (only changes during manual scaling). Should be cached with periodic refresh.

---

### 8. Circuit breaker config is frozen at startup (P2)

**File:** `OutboxPublisherService.cs:49-52`

Although `IOptionsMonitor` is used, the `TopicCircuitBreaker` is constructed once with the initial `failureThreshold` and `openDurationSeconds`. Runtime options changes (e.g., via config reload) don't propagate to the circuit breaker.

---

### 9. `Payload` column limited to 4000 chars — too small for production (P1)

**Files:** `install.sql` (both providers)

`NVARCHAR(4000)` / `VARCHAR(4000)` is insufficient for real-world event payloads (serialized aggregates, domain events with nested objects). Should be `NVARCHAR(MAX)` / `TEXT`.

---

### 10. No validation on `OutboxPublisherOptions` (P1)

Zero or negative values for `BatchSize`, `MinPollIntervalMs`, `CircuitBreakerFailureThreshold`, etc. would cause division-by-zero, infinite loops, or nonsensical behavior. There's no validation at startup or runtime.

---

### 11. SQL injection via `SchemaName` — SQL Server provider (P0)

**File:** `SqlServerOutboxStore.cs`

`_options.SchemaName` is string-interpolated directly into every SQL statement. A malicious or misconfigured value (e.g., `dbo]; DROP TABLE Outbox;--`) would execute arbitrary SQL. Should validate that `SchemaName` matches `^[a-zA-Z_][a-zA-Z0-9_]*$` or use `QUOTENAME()`.

---

### 12. SQL Server `RebalanceAsync` manages its own transaction inside `ExecuteWithRetryAsync` (P2)

**File:** `SqlServerOutboxStore.cs:371-443`

The SQL contains `BEGIN TRANSACTION` / `COMMIT TRANSACTION` as raw text, but is wrapped in `ExecuteWithRetryAsync` which opens a new connection per attempt. If the error occurs after `BEGIN TRANSACTION` but before `COMMIT`, SQL Server will rollback on connection close — which is correct — but if the error isn't caught by `IsTransientSqlError`, the retry won't fire. This is fragile. The PostgreSQL version handles this better with explicit `BeginTransactionAsync`.

---

### 13. `_options.CurrentValue` read multiple times per iteration without snapshot (P2)

**File:** `OutboxPublisherService.cs:82-87`

In `PublishLoopAsync`, `_options.CurrentValue` is read at the top of the loop body, but `pollIntervalMs` was set from a previous iteration's read. A hot-reload mid-iteration could change `MaxRetryCount` between the `LeaseBatchAsync` call and the poison-check filter, leading to inconsistent behavior.

---

## DESIGN / MAINTAINABILITY

### 14. Duplicated infrastructure code across providers (P3)

`ExecuteWithRetryAsync`, `OpenConnectionAsync`, `IsTransient*Error`, and `AddSequenceNumberTvp` are copy-pasted between the Store and DeadLetterManager in both providers (4 copies total). Bugs fixed in one copy may not be fixed in the others.

---

### 15. PostgreSQL uses `NOW()` vs DDL uses `clock_timestamp()` (P3)

In PostgreSQL, `NOW()` returns the transaction-start time (same value for entire transaction). The DDL defaults use `clock_timestamp()` (actual wall-clock time). Within multi-statement transactions like `RebalanceAsync`, all `NOW()` calls return the same timestamp — this could cause grace period calculations to be slightly off.

---

### 16. `IOutboxTransport` extends `IAsyncDisposable` but transport is never explicitly disposed (P3)

The `OutboxPublisherService` never disposes the transport. If registered as a singleton, it will be disposed by the DI container at shutdown, but the ordering relative to `UnregisterProducerAsync` is not guaranteed.

---

### 17. No health check integration (P3)

There's no `IHealthCheck` implementation. In production, you need to know if the publisher is alive, owns partitions, and is processing messages.

---

### 18. No structured logging with message/batch context (P3)

Log messages include topic name and count but not partition key, sequence numbers, or correlation IDs. Makes production debugging difficult.

---

## SUMMARY — Priority by Severity

| Priority | Issue | Type |
|----------|-------|------|
| **P0** | #1 Tight spin loop on DB failure | Bug |
| **P0** | #11 SQL injection via SchemaName | Security |
| **P0** | #3 EventHub batch disposal leak | Bug |
| **P1** | #9 Payload 4000 char limit | Design |
| **P1** | #4 Kafka partial-send duplicates | Reliability |
| **P1** | #5 EventHub ignores topicName | Bug |
| **P1** | #10 No options validation | Reliability |
| **P1** | #6 No retry on unregister | Reliability |
| **P2** | #7 Extra DB call per poll | Performance |
| **P2** | #2 False circuit breaker events | Bug |
| **P2** | #8 Frozen circuit breaker config | Design |
| **P2** | #12 SQL Server inline transaction | Reliability |
| **P2** | #13 Options snapshot inconsistency | Bug |
| **P3** | #14-18 Maintainability/observability | Design |

**Bottom line**: Solid POC with good architectural bones (partitioning, leasing, circuit breaker, dead-letter). However, 3 P0 issues (tight error loop, SQL injection, resource leak) must be fixed before production. The P1 issues around payload size limits, Kafka partial sends, and EventHub topic routing would also cause real production incidents.
