# Code Review — Outbox.Core / Outbox.SqlServer / Outbox.EventHub

**Review date:** 2026-04-18
**Reviewer:** Claude (Opus 4.7)
**Scope:** `src/Outbox.Core/`, `src/Outbox.SqlServer/`, `src/Outbox.EventHub/`
**Method:** Four parallel agents (one per project + one docs auditor), cross-referenced against `docs/outbox-requirements-invariants.md`, `docs/known-limitations.md`, and `CLAUDE.md`. Each finding below was verified by direct file inspection before inclusion.

Findings filtered to confidence ≥ 80 per the code-review rubric unless noted. Severity reflects production impact, not confidence.

## Summary

| #   | Severity   | Area              | Summary                                                                               |
| --- | ---------- | ----------------- | ------------------------------------------------------------------------------------- |
| 1   | High       | EventHub          | `EventDataBatch` leak when first message exceeds empty batch                          |
| 2   | High       | EventHub          | `ConcurrentDictionary.GetOrAdd` race can leak `EventHubProducerClient` (AMQP conn)    |
| 3   | High       | SQL Server        | `GetPendingCount` missing `WITH (NOLOCK)` — blocks under write pressure               |
| 4   | Medium     | Core              | `publishedAny = true` set before confirming any succeeded messages                    |
| 5   | Medium     | Core              | Integer overflow in heartbeat/health-check threshold arithmetic                       |
| 6   | Medium     | SQL Server        | Rebalance stale-partition marking is gated by `IF @ToAcquire > 0`                     |
| 7   | Medium     | SQL Server        | `SqlServerStoreOptions` snapshotted in ctor — hot-reload silently inert               |
| ~~~ | Withdrawn  | SQL Server        | ~~`FetchBatch` ORDER BY violates Store Contract~~ — see note below                    |
| D0  | Doc (High) | invariants        | Stale "one hub per transport instance" constraint — code supports multi-hub           |
| D1  | Doc (High) | Core README       | "Five concurrent loops" contradicts table / CLAUDE.md / code                          |
| D2  | Doc (High) | Core README       | Retry backoff defaults listed wrong (500/5000 vs actual 100/2000)                     |
| D3  | Doc (High) | known-limitations | Claims no `IValidateOptions<T>` — both validators exist                               |
| D4  | Doc (Med)  | invariants        | `RowVer` vs actual column name `RowVersion`                                           |
| D5  | Doc (Med)  | invariants        | Retry budget formula uses unqualified `MaxAttempts` (mixes store + publisher options) |
| D6  | Doc (Low)  | SQL README        | Rebalance description presents all 4 steps as unconditional                           |

No Critical correctness bugs were found in `Outbox.Core/Engine/OutboxPublisherService.cs` or `TopicCircuitBreaker.cs`. The main-line invariants (attempt-counter gating, circuit-open DLQ re-check, `CancellationToken.None` for cleanup, handler try/catch isolation, health state ordering, linked-CTS loop coordination, `PartialSendException` caught before generic `Exception`) all verified clean.

---

## 1 · EventHub: `EventDataBatch` leak when the first message is too large for a fresh batch

**Severity:** High — **Confidence:** 90
**File:** `src/Outbox.EventHub/EventHubOutboxTransport.cs:61-106,133`

Trace when a single message exceeds `MaxBatchSizeBytes` and the surrounding batch is empty:

```csharp
// line 61
batch = await client.CreateBatchAsync(batchOptions, ct);
foreach (var msg in messages)
{
    …
    if (!batch.TryAdd(eventData))               // line 86 — false
    {
        if (batch.Count > 0)                    // line 89 — false (empty)
        {
            await client.SendAsync(batch, ct);
            …
            batch.Dispose();
            batch = null;
            currentBatchSequenceNumbers.Clear();
        }
        // FALLS THROUGH without disposing the original empty batch
        sendCts.CancelAfter(…);
        batch = await client.CreateBatchAsync(batchOptions, ct);  // line 100 — overwrites `batch`
        if (!batch.TryAdd(eventData))            // line 102 — still false
        {
            throw new InvalidOperationException(…); // line 104
        }
    }
…
finally { batch?.Dispose(); }                    // line 133 — disposes only the SECOND batch
```

`EventDataBatch` implements `IDisposable` and owns a native AMQP message buffer. The first batch allocated at line 61 becomes unreachable when line 100 reassigns `batch`, and the `finally` only sees the current value. The leak is deterministic whenever a single message exceeds the configured batch size limit.

**Recommendation:** Always dispose the current batch before overwriting the variable:

```csharp
if (batch.Count > 0)
{
    await client.SendAsync(batch, ct);
    sentSequenceNumbers.AddRange(currentBatchSequenceNumbers);
    currentBatchSequenceNumbers.Clear();
}
batch.Dispose();               // NEW — unconditional
batch = null;
sendCts.CancelAfter(TimeSpan.FromSeconds(_sendTimeoutSeconds));
batch = await client.CreateBatchAsync(batchOptions, ct);
```

---

## 2 · EventHub: `ConcurrentDictionary.GetOrAdd` factory race can leak `EventHubProducerClient`

**Severity:** High — **Confidence:** 85
**File:** `src/Outbox.EventHub/EventHubOutboxTransport.cs:44`

```csharp
var client = _clients.GetOrAdd(topicName, name => _clientFactory(name));
```

`ConcurrentDictionary<TKey,TValue>.GetOrAdd(TKey, Func<TKey,TValue>)` is explicitly documented to invoke the factory _more than once_ when multiple threads race on the same absent key. Only one return value is stored; the others are silently abandoned.

`EventHubProducerClient` opens an AMQP connection on first `SendAsync`. Abandoned instances are never returned from the dictionary, never appear in `DisposeAsync` iteration (line 158), and never have `CloseAsync` called — the native connection stays up until GC finalizes the client (which, being managed, may not close the network handle cleanly).

With `PublishThreadCount = 4` default, two workers hitting a new topic concurrently is realistic on first deploy after a cold start.

**Recommendation:** Use the `Lazy<T>` idiom to guarantee single instantiation:

```csharp
private readonly ConcurrentDictionary<string, Lazy<EventHubProducerClient>> _clients = new();
…
var client = _clients.GetOrAdd(
    topicName,
    name => new Lazy<EventHubProducerClient>(
        () => _clientFactory(name),
        LazyThreadSafetyMode.ExecutionAndPublication)).Value;
```

Update `DisposeAsync` to iterate `_clients.Values.Where(l => l.IsValueCreated).Select(l => l.Value)` to avoid forcing lazy init during shutdown.

---

## ~~3~~ · WITHDRAWN — SQL Server `FetchBatch` ORDER BY

Originally flagged as a Store Contract violation on `SqlServerQueries.cs:82` (`ORDER BY o.PartitionId, o.SequenceNumber`). **Retracted after running actual EXPLAIN on both databases.**

Measured on SQL Edge / PG 16 with 50k rows and 32 owned partitions (`SET STATISTICS IO ON`):

| Query variant                            | SQL Server             | PostgreSQL              |
| ---------------------------------------- | ---------------------- | ----------------------- |
| `ORDER BY PartitionId, SequenceNumber`   | 7 logical reads, Index Seek on `IX_Outbox_Pending` | 67ms, Seq Scan + Sort on `outbox` (no composite index exists) |
| `ORDER BY SequenceNumber` (Store Contract) | 338 logical reads, Clustered-PK scan with per-row partition lookup | 0.4ms, Index Scan on `pk_outbox` |

Each store's current `ORDER BY` aligns with its index layout: SQL Server has `IX_Outbox_Pending (PartitionId, SequenceNumber)` as a covering nonclustered index, so the composite sort is free; PostgreSQL has `ix_outbox_pending (sequence_number)` single-column because `partition_id` is computed per query from `hashtext(partition_key)` and isn't a stored column. The per-`(topic, partition_key)` ordering invariant is preserved under both variants because rows sharing a `partition_key` share a `partition_id` by construction — the outer `PartitionId` sort on SQL Server never interleaves two keys.

The Store Contract in `docs/outbox-requirements-invariants.md` §1 was the thing that needed fixing, not the SQL. Doc updated to describe the per-store `ORDER BY` as an index-alignment choice, and `src/Outbox.SqlServer/README.md` gained a paragraph explaining why the composite sort is optimal.

---

## 3 · SQL Server: `GetPendingCount` query missing `WITH (NOLOCK)`

**Severity:** High — **Confidence:** 85
**File:** `src/Outbox.SqlServer/SqlServerQueries.cs:252`

```sql
SELECT COUNT_BIG(*) FROM {outboxTable};
```

No NOLOCK hint. Under `READ COMMITTED`, the full-table scan takes shared locks and blocks on any uncommitted INSERT. The requirements doc explicitly mandates NOLOCK on the outbox table (Store Contract #1; Anti-Pattern #9) and states:

> Without it, `READ COMMITTED` takes shared locks during scanning and blocks on rows with exclusive locks from uncommitted transactions, causing timeouts.

The FetchBatch query at line 73 correctly uses `WITH (NOLOCK)`. `GetPendingCount` is the only read path that forgot. It is called from the publish loop for gauge metrics, so under sustained INSERT load it can contribute to publish-loop delays.

**Recommendation:** `SELECT COUNT_BIG(*) FROM {outboxTable} WITH (NOLOCK);`

---

## 4 · Core: `publishedAny = true` set before confirming any succeeded messages

**Severity:** Medium — **Confidence:** 82
**File:** `src/Outbox.Core/Engine/OutboxPublisherService.cs:456`

Inside the `catch (PartialSendException pex)` handler:

```csharp
// line 455
_instrumentation.PublishFailures.Add(1);
publishedAny = true;                  // unconditional
lastError = pex.InnerException ?? pex;

var succeededSet = pex.SucceededSequenceNumbers.ToHashSet();
var succeeded = remaining.Where(m => succeededSet.Contains(m.SequenceNumber)).ToList();
if (succeeded.Count > 0)              // line 462 — only NOW do we know anything was sent
{
    …
}
```

If a transport emits `PartialSendException` with an empty `SucceededSequenceNumbers` list (zero messages actually delivered — a transport contract violation), `publishedAny` is still set `true`. That value propagates to the outer publish loop, which uses it to reset `pollIntervalMs` to `MinPollIntervalMs`, bypassing adaptive backoff. A repeatedly-misbehaving transport spins at 100ms polling.

Anti-Pattern #2 ("Tight loops without backoff") applies: the core should be defensive even if the transport is not.

**Recommendation:** Move the assignment into the `if (succeeded.Count > 0)` block, or gate on `succeededSet.Count > 0`.

---

## 5 · Core: integer overflow in options validation and health-check thresholds

**Severity:** Medium — **Confidence:** 88
**Files:** `src/Outbox.Core/Options/OutboxPublisherOptions.cs:78,81`; `src/Outbox.Core/Observability/OutboxHealthCheck.cs:52,73`

Four separate `int * int` expressions operate on fields declared with `[Range(1, int.MaxValue)]`:

| Expression                                       | Overflow at                           | Consequence                                                                                           |
| ------------------------------------------------ | ------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| `HeartbeatTimeoutSeconds * 1000`                 | `HeartbeatTimeoutSeconds ≥ 2_147_484` | `heartbeatTimeoutMs > 0` check in validator silently skips the constraint                             |
| `HeartbeatIntervalMs * 3`                        | `HeartbeatIntervalMs > 715_827_882`   | Same — validator skips                                                                                |
| `opts.HeartbeatIntervalMs * 3` (health check:52) | Same                                  | Negative threshold → `TimeSpan.FromMilliseconds(negative)` → health check reports permanent Unhealthy |
| `opts.MaxPollIntervalMs * 3` (health check:73)   | Same                                  | Same                                                                                                  |

Practical exposure is low (nobody sets heartbeat to months), but the health-check overflow produces a silent, misleading false-Unhealthy that would be very confusing to debug.

**Recommendation:** Cast one operand to `long` at each multiplication site:

```csharp
var heartbeatTimeoutMs = (long)HeartbeatTimeoutSeconds * 1000;
…
if (heartbeatTimeoutMs < (long)HeartbeatIntervalMs * 3) …
```

---

## 6 · SQL Server: Rebalance stale-partition marking gated by `IF @ToAcquire > 0`

**Severity:** Medium — **Confidence:** 82
**File:** `src/Outbox.SqlServer/SqlServerQueries.cs:152-166`

The UPDATE that sets `GraceExpiresUtc` on dead publishers' partitions is inside the `IF @ToAcquire > 0` block. When the calling publisher already owns ≥ fair share (e.g., it's in the release path), this UPDATE is skipped entirely. The separate `ClaimOrphanPartitions` query (line 210) only claims `WHERE OwnerPublisherId IS NULL` — it does not grace-mark stale-but-not-NULL owners.

Mathematical analysis: if any stale partitions exist, the sum of active-publisher ownership < total, so at least one active publisher must have `@ToAcquire > 0` and will mark stale partitions on its next rebalance cycle (default 30s). So the invariant "Orphan partitions MUST eventually be claimed" technically holds — but recovery latency can span multiple rebalance cycles, and the behavior is fragile against future refactors (e.g., if the overcrowded publisher is the only one running for a brief window, no grace marking happens at all).

**Recommendation:** Move the stale-publisher UPDATE above / outside the `IF @ToAcquire > 0` gate so every rebalance cycle contributes to stale detection, regardless of whether the caller is acquiring or releasing. It's a cheap UPDATE that touches at most the dead publishers' rows.

Also worth checking: `src/Outbox.SqlServer/README.md:78-84` presents all four rebalance steps as unconditional, which doesn't match the query structure (see D6 below).

---

## 7 · SQL Server: `SqlServerStoreOptions` snapshotted at constructor time

**Severity:** Medium — **Confidence:** 80
**File:** `src/Outbox.SqlServer/SqlServerOutboxStore.cs:40` (same pattern in `SqlServerDeadLetterManager.cs:24`)

```csharp
_options = optionsMonitor.Get(_optionsName);   // one-time snapshot
```

`_publisherOptions` is kept as `IOptionsMonitor<>` and consulted live via `.Get(...)` in `RebalanceAsync` / `ClaimOrphanPartitionsAsync`. But `_options` (the store-level settings — `CommandTimeoutSeconds`, `TransientRetryMaxAttempts`, `TransientRetryBackoffMs`, `SchemaName`, `TablePrefix`) is captured once and never re-read.

`docs/known-limitations.md` documents "Options hot-reload support" for the publisher and includes a "Snapshot-per-method guarantee" section — implying hot-reload is a system-wide property. Store options silently not participating is an undocumented asymmetry. An operator raising `CommandTimeoutSeconds` during a DB degradation will see no effect until a pod restart.

Schema/table-prefix changes genuinely should NOT be hot-reloaded (connections, TVP types, query strings are all bound to them), but timeout/retry settings are safe to reread per-call.

**Recommendation:** Either (a) store `_optionsMonitor` as a field and call `_optionsMonitor.Get(_optionsName)` at the top of each method that uses runtime-sensitive values (the same pattern as `_publisherOptions`), or (b) explicitly document in `known-limitations.md` that `SqlServerStoreOptions` / `EventHubTransportOptions` changes require a restart.

---

## Documentation findings

All doc findings were confirmed by direct file inspection.

### D0 · invariants — stale "one hub per transport instance" constraint

**File:** `docs/outbox-requirements-invariants.md:119` — **Confidence:** 100

> **Topic name validation: EventHub supports only one hub per transport instance.**

The code does the opposite, by design. `EventHubOutboxTransport` caches one `EventHubProducerClient` per distinct `topicName` via `ConcurrentDictionary<string, EventHubProducerClient>` (`src/Outbox.EventHub/EventHubOutboxTransport.cs:15,44`), `EventHubTransportOptions` has no `EventHubName` field (`src/Outbox.EventHub/EventHubTransportOptions.cs:5-10`), and `EventHubOutboxTransportSendTests.cs:51-62` explicitly asserts that sending to two different topics from the same transport does not throw. The Azure SDK requires one `EventHubProducerClient` per hub, so the per-topic cache is the correct shape — multi-hub is not a correctness risk.

Nothing about the at-least-once / per-key ordering guarantees requires single-hub, and the Kafka transport follows the same multi-topic-per-producer pattern without any analogous invariant. The single-hub constraint was most likely true in an earlier version and was silently relaxed. `known-limitations.md` does not document it as intentional.

**Recommendation:** Delete line 119 from `outbox-requirements-invariants.md`. Optionally add a short note to `known-limitations.md` stating that a single `EventHubOutboxTransport` instance supports multiple hubs (one `EventHubProducerClient` per topic, cached per-instance) and that per-hub tuning of `MaxBatchSizeBytes` / `SendTimeoutSeconds` is not possible — consumers needing per-hub tuning should register multiple transports via named groups.

### D1 · Core README — "runs five concurrent loops"

**File:** `src/Outbox.Core/README.md:110` — **Confidence:** 100

> A sealed `BackgroundService` that runs **five** concurrent loops:

…followed by a table of **four** rows (Publish, Heartbeat, Rebalance, Orphan sweep). `CLAUDE.md` also says "4 parallel loops." `OutboxPublisherService.ExecuteAsync` constructs exactly 4 tasks. Typo — should read "four".

### D2 · Core README — wrong retry defaults

**File:** `src/Outbox.Core/README.md:158-159` — **Confidence:** 100

README table says `RetryBackoffBaseMs = 500` and `RetryBackoffMaxMs = 5000`. Actual defaults in `src/Outbox.Core/Options/OutboxPublisherOptions.cs:30,37` are `100` and `2000`. Operators binding `Outbox:Publisher` from `appsettings.json` and expecting the documented defaults will get unexpectedly aggressive retries. **Fix: update README to `100` and `2000`.**

### D3 · known-limitations.md — "no `IValidateOptions<T>` implementation" is false

**File:** `docs/known-limitations.md:110-116` — **Confidence:** 100

> `KafkaTransportOptions.BootstrapServers` and `EventHubTransportOptions.ConnectionString` default to empty strings with **no `IValidateOptions<T>` implementation**.

Both `EventHubTransportOptionsValidator` (`src/Outbox.EventHub/EventHubTransportOptionsValidator.cs`) and `KafkaTransportOptionsValidator` exist, and both are registered via `TryAddEnumerable` in their builder extensions (`src/Outbox.EventHub/EventHubOutboxBuilderExtensions.cs:23-24`). They validate _timing_ relationships, not the connection string — which is the doc's actual point — but the section title and first sentence are flatly wrong and should be rewritten. Suggested rewrite: _"Neither validator checks `BootstrapServers` / `ConnectionString` for non-emptiness or network reachability — misconfigured connection values surface only on first publish attempt."_

### D4 · invariants — `RowVer` vs `RowVersion`

**File:** `docs/outbox-requirements-invariants.md:127` — **Confidence:** 100

Doc says `RowVer < MIN_ACTIVE_ROWVERSION()`. Actual column name in `SqlServerQueries.cs:81` and `src/Outbox.SqlServer/README.md:69` is `RowVersion`. Purely cosmetic but confusing for anyone grepping the codebase from the invariants doc.

### D5 · invariants — retry-budget formula uses unqualified `MaxAttempts`

**File:** `docs/outbox-requirements-invariants.md:145` — **Confidence:** 85

Configuration Constraints table row:

> `TransientRetryBackoffMs * 2^(MaxAttempts-1) > 20000` — Retry budget must cover DB failover

`TransientRetryBackoffMs` and `MaxAttempts` (actually `TransientRetryMaxAttempts`) belong to `SqlServerStoreOptions`, not `OutboxPublisherOptions`. The table doesn't label which options class each row refers to, and publishers could reasonably read "MaxAttempts" as `MaxPublishAttempts` from the publisher options. `src/Outbox.SqlServer/README.md:130-134` formulates this correctly with the full property name. Fix: qualify as `TransientRetryMaxAttempts` and add a note that this row pertains to `SqlServerStoreOptions`.

### D6 · SQL Server README — Rebalance description doesn't reflect code gating

**File:** `src/Outbox.SqlServer/README.md:78-84` — **Confidence:** 75

README lists the rebalance as a 4-step sequential batch:

> 1. Compute fair share. 2. Mark stale publishers' partitions with grace period. 3. Claim unowned or grace-expired partitions. 4. Release excess.

The query actually gates steps 2+3 behind `IF @ToAcquire > 0` and step 4 behind `IF @CurrentlyOwned > @FairShare` (see finding #6 above). Not a bug, but if step 2 moves out of the acquire gate (the recommended fix), the README will become accurate by accident.

---

## Areas verified clean

**Core / OutboxPublisherService:** `PartialSendException` caught before generic `Exception`; `CancellationToken.None` used for all post-delivery cleanup (`DeletePublishedAsync`, `DeadLetterAsync`, `UnregisterPublisherAsync`); health state transitions precede event-handler callbacks; `SetPublishLoopRunning(true)` zeros timestamp indicators; DLQ branch re-checks `circuitBreaker.IsOpen(topic)` after retry-loop exit (`OutboxPublisherService.cs:552`); event handlers individually try/catch-wrapped; linked CTS cancels all loops when any exits; attempt counter only incremented via `ClassifyAndRecord` on non-transient errors.

**Core / TopicCircuitBreaker:** all state transitions (Closed↔Open↔HalfOpen) serialized under per-entry lock; `IsOpen/RecordFailure/RecordSuccess` all acquire the lock; half-open probe uses atomic `ProbeInFlight` flag; `FailureCount` only reset on `RecordSuccess`.

**Core / Observability:** `OutboxHealthState` uses `Volatile.Read/Write` on `long` timestamps and `volatile` on booleans; `_openCircuits` `HashSet` protected by `_circuitLock`; `OutboxInstrumentation` uses `Interlocked.CompareExchange` to guard against double gauge registration.

**Core / Builder:** `TryAddEnumerable` used for unnamed-group interceptor dedup; keyed singletons correct for group scoping; no scoped-from-singleton antipatterns observed.

**Core / Models:** `PartialSendException` exposes both sequence-number sets; `OutboxMessage` carries `PayloadContentType`; `DeadLetteredMessage` carries `AttemptCount` and `PayloadContentType`.

**SQL Server / FetchBatch:** `WITH (NOLOCK)` present, `MIN_ACTIVE_ROWVERSION()` filter present, pure SELECT, no UPDLOCK/ROWLOCK/READPAST on the outbox table. `ORDER BY PartitionId, SequenceNumber` is the correct choice for the `IX_Outbox_Pending` composite index (verified by EXPLAIN — see withdrawn finding #3).

**SQL Server / DeletePublished + DeadLetter:** neither checks lease_owner; DeadLetter uses atomic `DELETE...OUTPUT...INTO`; `@AttemptCount` and `payload_content_type` propagated correctly.

**SQL Server / Heartbeat:** both UPDATEs in a single C#-managed transaction; `await using` of the transaction handles rollback on any exception.

**SQL Server / Transient retry:** exhaustive error-number list covers 1205 (deadlock), timeouts, Azure SQL failover codes, socket errors; exponential backoff with jitter; `CallAsync<T>` → `ExecuteWithRetryAsync` wraps all store operations.

**SQL Server / Partition count caching:** `Volatile.Read/Write` on both the count and the refresh-ticks; 60-second TTL as required.

**EventHub / IsTransient:** correctly classifies `EventHubsException.IsTransient`, timeouts, and socket-wrapped exceptions. `AggregateException` handled via inner-exceptions recursion.

**EventHub / DisposeAsync:** does NOT dispose injected dependencies — closes only the internally-created producer clients (the `ConcurrentDictionary` race in finding #2 is the exception).

**EventHub / Send atomicity:** `sentSequenceNumbers` accumulates only after successful `SendAsync` of each sub-batch; `PartialSendException` correctly thrown with populated success/failure sets on mid-batch exception.

---

## Recommendations by priority

**Ship before next release:**

- Fix #1, #2 (EventHub resource leaks)
- Fix #3 (`GetPendingCount` NOLOCK) — trivial one-line change, prevents user-visible slowness
- Address D0, D2 (stale invariant + wrong config defaults — users rely on these)

**Should fix in this cycle:**

- #4, #5 (Core defensive bugs)
- D1, D3 (doc accuracy)

**Nice to have:**

- #6, #7 (Medium issues, workarounds exist)
- D4, D5, D6 (doc polish)
