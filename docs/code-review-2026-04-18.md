# Code Review — Outbox.Core / Outbox.SqlServer / Outbox.EventHub

**Review date:** 2026-04-18
**Reviewer:** Claude (Opus 4.7)
**Scope:** `src/Outbox.Core/`, `src/Outbox.SqlServer/`, `src/Outbox.EventHub/`
**Method:** Four parallel agents (one per project + one docs auditor), cross-referenced against `docs/outbox-requirements-invariants.md`, `docs/known-limitations.md`, and `CLAUDE.md`. Each finding below was verified by direct file inspection before inclusion.

Findings filtered to confidence ≥ 80 per the code-review rubric unless noted. Severity reflects production impact, not confidence.

## Summary

All findings resolved. No open issues.

No Critical correctness bugs were found in `Outbox.Core/Engine/OutboxPublisherService.cs` or `TopicCircuitBreaker.cs`. The main-line invariants (attempt-counter gating, circuit-open DLQ re-check, `CancellationToken.None` for cleanup, handler try/catch isolation, health state ordering, linked-CTS loop coordination, `PartialSendException` caught before generic `Exception`) all verified clean.

---

## Areas verified clean

**Core / OutboxPublisherService:** `PartialSendException` caught before generic `Exception`; `CancellationToken.None` used for all post-delivery cleanup (`DeletePublishedAsync`, `DeadLetterAsync`, `UnregisterPublisherAsync`); health state transitions precede event-handler callbacks; `SetPublishLoopRunning(true)` zeros timestamp indicators; DLQ branch re-checks `circuitBreaker.IsOpen(topic)` after retry-loop exit (`OutboxPublisherService.cs:552`); event handlers individually try/catch-wrapped; linked CTS cancels all loops when any exits; attempt counter only incremented via `ClassifyAndRecord` on non-transient errors.

**Core / TopicCircuitBreaker:** all state transitions (Closed↔Open↔HalfOpen) serialized under per-entry lock; `IsOpen/RecordFailure/RecordSuccess` all acquire the lock; half-open probe uses atomic `ProbeInFlight` flag; `FailureCount` only reset on `RecordSuccess`.

**Core / Observability:** `OutboxHealthState` uses `Volatile.Read/Write` on `long` timestamps and `volatile` on booleans; `_openCircuits` `HashSet` protected by `_circuitLock`; `OutboxInstrumentation` uses `Interlocked.CompareExchange` to guard against double gauge registration.

**Core / Builder:** `TryAddEnumerable` used for unnamed-group interceptor dedup; keyed singletons correct for group scoping; no scoped-from-singleton antipatterns observed.

**Core / Models:** `PartialSendException` exposes both sequence-number sets; `OutboxMessage` carries `PayloadContentType`; `DeadLetteredMessage` carries `AttemptCount` and `PayloadContentType`.

**SQL Server / FetchBatch:** `WITH (NOLOCK)` present, `MIN_ACTIVE_ROWVERSION()` filter present, pure SELECT, no UPDLOCK/ROWLOCK/READPAST on the outbox table. `ORDER BY PartitionId, SequenceNumber` aligns with the composite nonclustered index `IX_Outbox_Pending` and is ~48× more efficient than a bare `ORDER BY SequenceNumber` (verified by EXPLAIN on 50k rows).

**SQL Server / DeletePublished + DeadLetter:** neither checks lease_owner; DeadLetter uses atomic `DELETE...OUTPUT...INTO`; `@AttemptCount` and `payload_content_type` propagated correctly.

**SQL Server / Heartbeat:** both UPDATEs in a single C#-managed transaction; `await using` of the transaction handles rollback on any exception.

**SQL Server / Transient retry:** exhaustive error-number list covers 1205 (deadlock), timeouts, Azure SQL failover codes, socket errors; exponential backoff with jitter; `CallAsync<T>` → `ExecuteWithRetryAsync` wraps all store operations.

**SQL Server / Partition count caching:** `Volatile.Read/Write` on both the count and the refresh-ticks; 60-second TTL as required.

**EventHub / SendAsync:** `EventDataBatch` always disposed before reassignment; `EventHubProducerClient` cache uses `Lazy<T>` to guarantee single instantiation under concurrent first-access; `sentSequenceNumbers` accumulates only after successful `SendAsync` of each sub-batch; `PartialSendException` correctly thrown with populated success/failure sets on mid-batch exception.

**EventHub / IsTransient:** correctly classifies `EventHubsException.IsTransient`, timeouts, and socket-wrapped exceptions. `AggregateException` handled via inner-exceptions recursion.

**EventHub / DisposeAsync:** does NOT dispose injected dependencies — closes only the internally-created producer clients (skipping non-materialized `Lazy<T>` entries so shutdown never forces instantiation).

