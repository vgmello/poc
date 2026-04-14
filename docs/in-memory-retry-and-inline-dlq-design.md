# Design Spec: In-Memory Retry and Inline Dead-Lettering

**Status:** Approved design. Ready for implementation planning.
**Date:** 2026-04-14
**Scope:** `OutboxPublisherService` retry/DLQ behavior, `IOutboxStore` and `IOutboxTransport` contracts, `OutboxMessage` model, schema for the outbox and dead-letter tables.

---

## Motivation

Today the publisher tracks per-message retry state in a `RetryCount` column on the outbox table. Every transport failure issues an `UPDATE ... SET RetryCount = RetryCount + 1`, and a separate `DeadLetterSweepLoop` runs every minute to find rows where `RetryCount >= MaxRetryCount` and move them to the dead-letter table. Three problems with this model:

1. **DB write amplification under failure.** A broker outage that affects N messages produces N retry-count UPDATEs per poll on top of the failed sends, throughout the outage.
2. **No transient/permanent classification.** Every transport failure burns a retry, including connection timeouts and broker-unreachable errors that have nothing to do with the message itself. This drains the retry budget on transient infrastructure problems and pushes healthy messages toward the DLQ during outages.
3. **Out-of-order DLQing during outages.** When many messages fail concurrently because of a broker problem, the sweep loop moves them to the DLQ in whatever order it encounters them, not in `sequence_number` order. Worse, those DLQs happen at all — the right outcome during an outage is "leave the messages alone and try again later," not "dead-letter everything."

This design moves retry tracking entirely in-memory, makes the publisher classify transport errors as transient or non-transient, and dead-letters inline within the publish loop. The dead-letter sweep loop, the `IncrementRetryCount` operation, and the `RetryCount` column on the outbox table are removed.

---

## Behavioral Model

The publish loop's structure is unchanged at the top level: `FetchBatchAsync` returns a batch, the batch is grouped by `(topicName, partitionKey)`, groups are distributed to worker threads by partition affinity, each worker processes its assigned groups sequentially. The change is inside `ProcessGroupAsync`, which is rewritten around an in-memory retry loop:

```text
attempt = 0
remaining = group.OrderBy(EventDateTimeUtc, EventOrdinal, SequenceNumber).ToList()
lastError = null

while attempt < MaxPublishAttempts and not cancellation:
    if circuitBreaker.IsOpen(topic):
        # Outage in progress. Leave remaining messages in the outbox.
        # OnPublishFailedAsync(remaining, lastError, CircuitOpened)
        return

    try:
        await transport.SendAsync(topic, partitionKey, remaining, ct)

        await deletePublished(remaining)
        circuitBreaker.RecordSuccess(topic)
        # OnMessagePublishedAsync(msg) for each msg
        return

    except PartialSendException as pex:
        # Some succeeded, some failed.
        await deletePublished(pex.Succeeded)
        remaining = remaining.Where(m => pex.Failed.Contains(m.SequenceNumber)).ToList()
        lastError = pex.InnerException
        # Fall through to classification using lastError

    except Exception as ex:
        lastError = ex

    # Classify the failure
    if transport.IsTransient(lastError):
        circuitBreaker.RecordFailure(topic)   # may open the circuit
        # Do NOT increment attempt
    else:
        attempt += 1
        # Do NOT record a circuit failure

    if attempt < MaxPublishAttempts:
        await Task.Delay(BackoffFor(attempt), ct)

# Loop exited without full success and the circuit didn't open:
# remaining messages are poison.
await store.DeadLetterAsync(remaining.Select(m => m.SequenceNumber), attempt, lastError?.ToString(), CancellationToken.None)
# OnPublishFailedAsync(remaining, lastError, RetriesExhausted)
# OnMessageDeadLetteredAsync(msg) for each msg
```

Two invariants fall out of this structure:

- **DLQing only happens when retries exhaust via non-transient errors.** A transient outage exits the loop through the circuit-open branch, not the DLQ branch. There is no longer a configuration constraint coupling `MaxPublishAttempts` to `CircuitBreakerFailureThreshold` — the structural rule replaces it.
- **Per-(topic, partitionKey) ordering is preserved.** One group at a time per worker, retries stay within the group, and DLQing the failed subset preserves their `sequence_number` order in the DLQ table. The next group for a different partition key is processed after this one finishes.

### Failure classification

Each transport implementation knows its broker's exception types best, so the classifier lives on the transport contract. The publisher does not pattern-match on broker-specific exception types; it asks the transport.

- **Transient errors** (broker is sick): network timeouts, connection failures, broker-unreachable, throttling, leader election in progress. Do NOT burn the attempt counter; DO record a circuit failure.
- **Non-transient errors** (message is bad): schema validation failures, oversized messages, authorization rejection, protocol-level rejections. DO burn the attempt counter; do NOT record a circuit failure.
- **Default for unknown exception types**: non-transient. This biases toward DLQing over infinite retry, which is the safer default for an outbox library.

### Backoff

Between attempts of the same group, the publisher sleeps `min(RetryBackoffBaseMs * 2^(attempt-1), RetryBackoffMaxMs)` on the cancellation token. Defaults: base `100ms`, max `2000ms`. The cancellation token is the linked publish-loop token, so shutdown interrupts the sleep cleanly and the loop exits without touching the messages.

The backoff applies regardless of whether the previous attempt was transient or non-transient. A transient failure that doesn't yet trip the circuit still backs off so we don't hammer a half-broken broker; a non-transient failure backs off because retrying instantly almost never helps.

---

## API and Contract Changes

### `IOutboxTransport`

```csharp
public interface IOutboxTransport
{
    Task SendAsync(string topic, string partitionKey,
        IReadOnlyList<OutboxMessage> messages, CancellationToken ct);

    // NEW
    bool IsTransient(Exception ex);
}
```

`KafkaTransport` and `EventHubTransport` each implement `IsTransient` based on their broker SDK's exception model. Sketch:

- **Kafka**: classify `KafkaException` by `Error.Code` against the set of transient codes (broker not available, leader not available, request timed out, network exception, throttling delay, etc.). Anything else returns `false`.
- **EventHub**: `ex is EventHubsException eh && eh.IsTransient`. Anything else returns `false`.

A unit test suite per transport pins the classification of representative exceptions to keep the rules stable.

### `IOutboxStore`

```csharp
public interface IOutboxStore
{
    string PublisherId { get; }
    Task<string> RegisterPublisherAsync(CancellationToken ct);
    Task UnregisterPublisherAsync(string publisherId, CancellationToken ct);

    // CHANGED — maxRetryCount parameter removed
    Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize, CancellationToken ct);

    Task DeletePublishedAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    // REMOVED
    // Task IncrementRetryCountAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct);
    // Task SweepDeadLettersAsync(string publisherId, int maxRetryCount, CancellationToken ct);

    // CHANGED — attemptCount parameter added
    Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers,
        int attemptCount,
        string? lastError,
        CancellationToken ct);

    Task HeartbeatAsync(string publisherId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct);
    Task RebalanceAsync(string publisherId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct);
    Task<long> GetPendingCountAsync(CancellationToken ct);
}
```

### `OutboxMessage`

```csharp
public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    Dictionary<string, string>? Headers,
    byte[] Payload,
    string PayloadContentType,
    DateTimeOffset EventDateTimeUtc,
    int EventOrdinal,
    // RetryCount removed
    DateTimeOffset CreatedAtUtc);
```

### `IOutboxEventHandler`

```csharp
public enum PublishFailureReason
{
    RetriesExhausted,    // Followed by OnMessageDeadLetteredAsync per message
    CircuitOpened,       // Messages stay in the outbox, will be retried later
    Cancelled            // Shutdown; messages stay in the outbox
}

public interface IOutboxEventHandler
{
    Task OnMessagePublishedAsync(OutboxMessage msg, CancellationToken ct);

    // CHANGED — fires once per loop exit, not once per attempt; new reason parameter
    Task OnPublishFailedAsync(
        IReadOnlyList<OutboxMessage> messages,
        Exception lastError,
        PublishFailureReason reason,
        CancellationToken ct);

    Task OnCircuitBreakerStateChangedAsync(string topic, CircuitState newState, CancellationToken ct);
    Task OnMessageDeadLetteredAsync(OutboxMessage msg, CancellationToken ct);
    Task OnRebalanceAsync(string publisherId, IReadOnlyList<int> ownedPartitions, CancellationToken ct);
}
```

### `OutboxPublisherService`

- `DeadLetterSweepLoopAsync` deleted entirely. The loop architecture goes from 5 loops to 4: publish, heartbeat, rebalance, orphan sweep. `RunLoopsWithRestartAsync` updates its task array accordingly.
- `ProcessGroupsAsync` rewritten around the in-memory retry loop in the Behavioral Model section. The current `IncrementRetryCountAsync` calls (general-exception path and `PartialSendException` path) are removed.
- `HandlePublishFailureAsync` is collapsed into the new loop. The circuit breaker calls and the (newly outcome-level) handler call happen at loop exit.
- All cleanup writes (`DeletePublishedAsync`, `DeadLetterAsync`) continue to use `CancellationToken.None` so they complete during shutdown — same pattern as today.

### `OutboxPublisherOptions`

- **`MaxRetryCount` renamed to `MaxPublishAttempts`** (default `5`). Documentation: "Maximum number of times a `(topic, partitionKey)` group will be sent before the failed messages are dead-lettered. This counts total attempts, including the first send. Only non-transient failures consume an attempt; transient failures (broker unreachable, timeouts, etc.) do not."
- **`DeadLetterSweepIntervalMs` removed.** No sweep loop.
- **Validation rule removed:** the old `MaxRetryCount > CircuitBreakerFailureThreshold` rule is gone. Under the clean separation of transient/non-transient signals, the two settings never interact.
- **New: `RetryBackoffBaseMs`** (default `100`). Initial backoff between retry attempts inside one group's processing.
- **New: `RetryBackoffMaxMs`** (default `2000`). Maximum backoff between retry attempts.

---

## Schema Changes

### Outbox table (both stores)

Drop the `RetryCount` column.

- SQL Server: `{schema}.{prefix}Outbox.RetryCount` (INT NOT NULL DEFAULT 0).
- PostgreSQL: `{schema}.{prefix}_outbox.retry_count` (integer NOT NULL DEFAULT 0).

Any indexes that reference the column are rebuilt without it. (The current schema does not index on `RetryCount`, but verify during implementation.)

### Dead-letter table (both stores)

Rename the existing `RetryCount` column to **`AttemptCount`**, same type. The semantic is now "the number of in-memory attempts at the time of dead-lettering," not "the durable retry counter from the outbox row." `DeadLetterAsync` writes this from the new `attemptCount` parameter.

### `FetchBatch` query (both stores)

Remove the `o.RetryCount` projection, the `AND o.RetryCount < @MaxRetryCount` filter, and the `@MaxRetryCount` parameter. Everything else (the `WITH (NOLOCK)` hint on SQL Server, the `RowVersion < MIN_ACTIVE_ROWVERSION()` ceiling, the partition ownership filter, the `ORDER BY`) is unchanged.

### `DeadLetter` query (both stores)

Remove the `deleted.RetryCount` projection from the `OUTPUT`/`RETURNING` clause. Add an `@AttemptCount` parameter and write it to the `AttemptCount` column of the dead-letter table.

### Migration

Per the project's "drop anything you need; treat the new schema as if it were always this way" guidance, no incremental migration scripts are required. The schema files are edited in place. Integration tests run against the new schema directly.

---

## Event Handler Semantics

| Handler | Firing rule |
|---|---|
| `OnMessagePublishedAsync` | Once per message after a successful transport send, before the delete. Unchanged. |
| `OnPublishFailedAsync` | **Once per group, at loop exit, when the group did not get fully delivered.** Carries a `PublishFailureReason` of `RetriesExhausted`, `CircuitOpened`, or `Cancelled`. Per-attempt failures during the loop are observable via metrics and logs; they do not fire this handler. |
| `OnCircuitBreakerStateChangedAsync` | On `Closed → Open`, `Open → HalfOpen`, `HalfOpen → Closed` transitions. Unchanged. |
| `OnMessageDeadLetteredAsync` | Once per message, immediately after a successful inline `DeadLetterAsync`. **No longer fired by a sweep loop** — that loop is gone. |
| `OnRebalanceAsync` | After a successful rebalance. Unchanged. |

### Failure-path ordering inside `ProcessGroupAsync`

The existing rule "health state updated before handler callbacks; each handler call is individually try/caught and never falls through to a transport-failure path" continues to apply. Concretely:

```text
On RetriesExhausted exit:
    1. await store.DeadLetterAsync(failed, attemptCount, lastError?.ToString(), CancellationToken.None)
    2. instrumentation.MessagesDeadLettered.Add(failed.Count)
    3. try { await handler.OnPublishFailedAsync(failed, lastError, RetriesExhausted, ct) } catch logged
    4. foreach msg in failed:
           try { await handler.OnMessageDeadLetteredAsync(msg, ct) } catch logged

On CircuitOpened exit:
    1. (circuit state was already updated by RecordFailure inside the loop)
    2. instrumentation.PublishFailures.Add(1)
    3. try { await handler.OnPublishFailedAsync(remaining, lastError, CircuitOpened, ct) } catch logged
    4. (no DLQ; remaining messages stay in the outbox)

On Cancelled exit:
    1. (no handler call; logs only)
```

`OnMessageDeadLetteredAsync` is wrapped per-message so a thrown exception from one poison message's handler does not block the next poison message's handler call or fall through to a transport-failure path. This matches anti-pattern #9 in `outbox-requirements-invariants.md`.

---

## Concurrency Model

Unchanged from today. Each worker thread (count = `PublishThreadCount`) owns a slice of the batch's groups, assigned by partition-affinity hash. Each worker processes its groups sequentially. The retry loop runs inside one worker's processing of one group, so retries for different groups happen on different threads in parallel. The circuit breaker's per-topic dictionary is the only shared mutable state and is already thread-safe via `lock (entry.Lock)` in `TopicCircuitBreaker.cs`.

The retry loop adds no new shared state. `attempt`, `remaining`, and `lastError` are all locals on the worker thread's stack.

---

## Testing Strategy

### Unit tests — `tests/Outbox.Core.Tests/`

A focused suite for `ProcessGroupAsync` against a fake `IOutboxTransport` with dials for transient/non-transient/partial-send/success on attempt N. Scenarios:

| Scenario | Expected outcome |
|---|---|
| Send succeeds on the first try | Group deleted; no retries; no handler events beyond `OnMessagePublishedAsync`. |
| Non-transient failure × 2, then success on attempt 3 | Group deleted; attempt counter reached 2; no DLQ; no circuit failures recorded. |
| Non-transient failure × `MaxPublishAttempts` | Group DLQed inline with `attemptCount = MaxPublishAttempts`; `OnPublishFailedAsync(RetriesExhausted)` fires; `OnMessageDeadLetteredAsync` fires per message; **no circuit failure recorded**. |
| Transient failure × `CircuitBreakerFailureThreshold` | Circuit opens; group stays in outbox; `OnPublishFailedAsync(CircuitOpened)` fires; **no DLQ**; **attempt counter not incremented**. |
| Transient failure indefinitely | Circuit opens before attempt counter reaches the limit; group is never DLQed. |
| Mixed: 2× transient, then 3× non-transient | Attempt counter incremented only on the 3 non-transient attempts; circuit failures recorded only on the 2 transient attempts. |
| `PartialSendException` (3 succeeded, 2 failed), then full success on retry | Succeeded subset deleted immediately; failed subset retried; both eventually deleted. |
| `PartialSendException` (3/5 succeeded) on attempt 1 with non-transient inner, then the failed subset of 2 fails non-transient on every subsequent attempt | Original 3 deleted immediately; remaining 2 DLQed when the loop exits; `attemptCount` written to the DLQ equals `MaxPublishAttempts` (the partial-send attempt counts as attempt 1, the failed-subset retries fill the remaining budget). |
| Cancellation mid-retry | Loop exits cleanly; no DLQ; no handler called; in-flight messages remain in the outbox. |

A second suite covers `KafkaTransport.IsTransient` and `EventHubTransport.IsTransient` against representative `KafkaException` and `EventHubsException` instances.

### Store unit tests — `tests/Outbox.Store.Tests/`

- `FetchBatchAsync` no longer takes `maxRetryCount` and no longer filters on retry; verify a row that would have been filtered out under the old contract is now returned.
- `DeadLetterAsync(seqs, attemptCount, lastError, ct)` writes `attemptCount` to the renamed `AttemptCount` column.
- `IncrementRetryCountAsync` and `SweepDeadLettersAsync` no longer exist on the contract; the test files that exercised them are deleted.

### Integration tests — `tests/Outbox.IntegrationTests/`

Update or add the following scenarios in `docs/failure-scenarios-and-integration-tests.md`:

1. **Poison message with healthy broker.** Insert a message the broker rejects (e.g., oversized). Assert: ends up in DLQ with `AttemptCount = MaxPublishAttempts`; no circuit transitions; other groups for the same topic continue flowing.
2. **Broker outage.** Block the broker port mid-publish. Assert: circuit opens; no messages are DLQed during the outage; messages resume after the broker comes back.
3. **Mixed poison + outage.** Poison message in flight when broker dies. Assert: circuit opens before the poison message exhausts its attempts; while the circuit is open, the poison message stays in the outbox; once the broker returns and the circuit closes, the poison message is re-fetched on the next poll cycle, gets a fresh attempt sequence, and eventually DLQs after exhausting it; healthy messages flow normally throughout.
4. **Publisher restart mid-retry.** Kill the publisher partway through a retry loop, restart it. Assert: the same group is re-fetched and gets a fresh attempt counter (in-memory state loss is the agreed behavior).
5. **DLQ ordering.** DLQ a sequence of poison messages for the same partition key. Assert: they appear in the DLQ table in `sequence_number` order.

### Performance tests — `tests/Outbox.PerformanceTests/`

Re-baseline against the new model. Expected outcomes:

- **Slightly higher throughput under healthy conditions** — one fewer DB round-trip per failed-then-succeeded message (no `IncrementRetryCount` UPDATE), no `SweepDeadLetters` periodic scan.
- **Dramatically lower DB load during outages** — no failed-attempt UPDATEs.

The 60-minute perf run is re-recorded for both stores after the change lands. `docs/postgresql-performance-results.md` and `docs/sqlserver-performance-results.md` are updated.

### What is explicitly NOT tested

- Retry count durability across publisher restarts. That property is removed by design.
- The dead-letter sweep loop. It no longer exists.
- The `MaxRetryCount > CircuitBreakerFailureThreshold` validation rule. It no longer exists.

---

## Anti-Patterns to Watch For

These follow as direct consequences of the design and should be added to the review checklist in `docs/outbox-requirements-invariants.md`.

1. **Burning the attempt counter on transient errors.** The whole point of the transient/non-transient split is to keep the attempt counter dedicated to message-level health. If a future change accidentally increments `attempt` on a transient catch, transient outages will start DLQing healthy messages again.
2. **Recording a circuit failure on non-transient errors.** Symmetrically, the circuit breaker is dedicated to broker-level health. A poison message that always fails non-transiently must not trip the circuit and stop the whole topic.
3. **DLQing while the circuit is open.** The retry loop must check `circuitBreaker.IsOpen(topic)` at the top of each attempt and exit through the `CircuitOpened` branch — never fall through to the DLQ branch when the circuit is already open.
4. **Firing `OnPublishFailedAsync` per attempt.** This handler is outcome-level. Per-attempt observability lives in the `PublishFailures` counter and structured logs. A per-attempt fire rule produces handler floods during outages.
5. **Reintroducing a long-lived in-memory retry dictionary.** The attempt counter is a local variable on the worker thread's stack, scoped to one group's processing. There is no dictionary to clean up. A future change that "promotes" the counter to a `ConcurrentDictionary<long, int>` keyed by sequence number reintroduces the cross-batch state model that this design rejects.
6. **Using the linked cancellation token for `DeadLetterAsync` or `DeletePublishedAsync` in the failure path.** Cleanup writes must use `CancellationToken.None` so they complete during shutdown. This is the existing pattern for `IncrementRetryCountAsync` cleanup, and continues to apply to `DeadLetterAsync` under the new model.

---

## Documentation Updates Required

The following docs need updates as part of the change. Listed for the implementation plan to track.

- `docs/outbox-requirements-invariants.md` — sections "Retry count accuracy", "Circuit breaker behavior", store contract items #3, #4, #9, plus the configuration constraints table. Add the new anti-patterns from the section above.
- `docs/known-limitations.md` — any text mentioning retry count durability across restarts.
- `docs/failure-scenarios-and-integration-tests.md` — update the scenarios listed under Testing Strategy.
- `CLAUDE.md` review checklist — replace the bullet "Retry count only incremented on transport failure (never on delete failure or circuit-open skip)" with "Attempt counter only incremented on non-transient transport failure; circuit failures only recorded on transient failures" and add a bullet for "DLQ never happens while the circuit is open."
- `src/Outbox.Core/README.md`, `src/Outbox.SqlServer/README.md`, `src/Outbox.PostgreSQL/README.md` — anywhere they describe retry/DLQ behavior or list store operations.

---

## Out of Scope

- The EF6 transactional outbox integration discussed alongside this work. That ships as a separate spec/plan after this one lands. It depends on the contract this spec finalizes, which is why it's sequenced second.
- Any change to the `event_datetime_utc` / `event_ordinal` ordering contract. See `docs/ordering-column-design-note.md` for the analysis of that separate question. This spec does not touch it.
- The partition-ownership model, heartbeat loop, rebalance logic, and orphan sweep. These remain as-is.
