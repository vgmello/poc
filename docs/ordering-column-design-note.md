# Design Note: Ordering Columns in FetchBatch

**Status:** Design analysis. Not an accepted change.
**Status update (2026-04-14):** Accepted and implemented. See `docs/sequence-number-ordering-implementation-plan.md` for the implementation plan and the resulting commits on the `feat/sequence-number-ordering` branch.
**Date:** 2026-04-10
**Scope:** `FetchBatch` ORDER BY clause on both PostgreSQL and SQL Server stores.

---

## Question

The current `FetchBatch` query orders by `event_datetime_utc, event_ordinal, sequence_number`. If we stipulate that **callers insert messages in the order they want them delivered**, can we simplify the ordering contract by dropping `event_datetime_utc` and `event_ordinal`?

A tempting first instinct is to order by the row version column that already exists on both stores (`RowVer` / `xmin`). **That instinct is wrong.** This document explains why, shows what the correct simplification looks like, and enumerates the costs of pursuing it.

---

## Current design recap

From `docs/outbox-requirements-invariants.md`:

> Within a `(topic, partitionKey)` group in a single batch: messages MUST be sent to the broker in `event_datetime_utc, event_ordinal, sequence_number` order.
>
> Across batches for the same partitionKey: ordering is guaranteed by the SQL `ORDER BY event_datetime_utc, event_ordinal` in FetchBatch, combined with a version ceiling filter (`xmin < pg_snapshot_xmin(...)` on PostgreSQL, `RowVer < MIN_ACTIVE_ROWVERSION()` on SQL Server) that withholds freshly inserted rows when earlier write transactions are still in-flight.

Two moving parts:

1. **Sort key** — `event_datetime_utc, event_ordinal, sequence_number`. Caller-asserted causal order, with `sequence_number` as a final deterministic tiebreaker.
2. **Ceiling filter** — `RowVer < MIN_ACTIVE_ROWVERSION()` / `xmin < pg_snapshot_xmin(...)`. Withholds rows whose write transaction is newer than the oldest still-in-flight transaction. Prevents the "tx #2 committed before tx #1, so we published #2's rows and then #1 showed up later" reorder.

The sort key is the *caller's* contract. The ceiling filter is the *database's* contract.

---

## Why `rowversion` / `xmin` as the sort key is a trap

### SQL Server: `rowversion` is bumped on UPDATE

`rowversion` (and its cousin `ROWVERSION` / `timestamp`) is a database-wide monotonic 8-byte counter assigned at **every write**, including updates. The existing code path performs retries via:

```sql
-- IncrementRetryCount
UPDATE o SET o.RetryCount = o.RetryCount + 1
FROM {outbox} o INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber;
```

This stamps the updated rows with new, higher rowversions. Consequence:

| Time | Event | E1.RowVer | E2.RowVer | E3.RowVer |
|------|-------|-----------|-----------|-----------|
| t0   | Insert E1, E2, E3 for `(topic=orders, key=abc)` | 100 | 101 | 102 |
| t1   | Publisher fetches {E1, E2, E3}, send fails on E1 | 100 | 101 | 102 |
| t2   | `IncrementRetryCount` on E1 | **200** | 101 | 102 |
| t3   | Publisher fetches with `ORDER BY RowVer`        | → E2, E3, E1 |

The next fetch returns the batch in **E2, E3, E1** order. **That corrupts per-key ordering**, which is the first and most important invariant in the review checklist (`CLAUDE.md`):

> **MESSAGE ORDERING MUST NEVER BE CORRUPTED.** Per-(topic, partitionKey) ordering is the core guarantee.

This is a hard no on SQL Server.

### PostgreSQL: `xmin` is not the right shape

PG's `xmin` is the **transaction ID** that created the row, not a per-row counter. All rows in one transaction share the same `xmin`. `xmin` alone can't order rows within a transaction — you'd still need `sequence_number` as a tiebreaker. Additionally:

- `xmin` is a system column; indexing it requires an expression index, which is non-standard and harder to reason about.
- `xmin` is 32-bit and subject to wraparound. The current ceiling filter already sidesteps this with `::text::bigint` conversions that are pragmatic but not something you want in your hot-path ORDER BY.
- Retry `UPDATE` on PG doesn't change `xmin` (PG creates a new row version with a new `xmax`, but the live tuple's `xmin` is still the original). So the SQL Server retry-reorder trap doesn't directly apply — but that *asymmetric behavior across stores* is itself a liability. You'd have one store silently reordering retries and the other not.

Neither store benefits from `xmin`/`rowversion` as the sort key.

---

## What would actually work: `ORDER BY sequence_number`

`sequence_number` is:

| Property | SQL Server | PostgreSQL |
|----------|-----------|------------|
| Type | `BIGINT IDENTITY` | `bigserial` (wraps `bigint` + sequence) |
| Monotonic at insert | Yes | Yes (via `nextval()`) |
| Immutable after insert | Yes | Yes |
| Already indexed | Yes (PK) | Yes (PK) |

Combined with the **existing** version-ceiling filter, `ORDER BY sequence_number` is sufficient to preserve per-key delivery order, under the stipulation that callers insert in order.

### Walk-through: cross-transaction commit reorder

Two transactions inserting for the same partition key:

| Time | Event | T1 state | T2 state |
|------|-------|----------|----------|
| t0   | T1 begins, inserts `(seq=100, key=abc)` | in-flight | — |
| t1   | T2 begins, inserts `(seq=101, key=abc)` | in-flight | in-flight |
| t2   | T2 commits | in-flight | committed |
| t3   | Publisher fetches | — | — |
| t4   | T1 commits | committed | committed |
| t5   | Publisher fetches | — | — |

At t3 (T2 committed, T1 still in-flight):
- `MIN_ACTIVE_ROWVERSION()` / `pg_snapshot_xmin()` = the value from T1 (T1 is still active).
- Row 101's RowVer / xmin is from T2's write, which is ≥ T1's still-active boundary, so it **fails the ceiling filter**. Row 100 is also in-flight (T1 hasn't committed), so it also fails. Publisher fetches nothing.

At t5 (both committed):
- Ceiling has moved past both. Both rows pass the filter.
- `ORDER BY sequence_number` → 100 first, then 101. Delivery respects insert order.

The ceiling filter is what makes the sort column irrelevant to commit-reorder safety. As long as the sort column is **immutable and monotonic at insert**, you're correct.

### Walk-through: same-transaction ordering

A single transaction inserting E1, E2, E3:

- SQL Server: each `INSERT` consumes one IDENTITY value in statement order. `sequence_number` is 100, 101, 102 for E1, E2, E3 respectively. Sort by sequence_number → E1, E2, E3. ✓
- PostgreSQL: each `INSERT` calls `nextval()` in statement order, yielding 100, 101, 102. Same result. ✓

`event_ordinal` adds no information here — sequence_number is already a deterministic tiebreaker, assigned in insert order.

### Walk-through: retry reorder (the trap that killed rowversion)

Back to the E1-fails-and-is-retried scenario:

| Time | Event | E1.seq | E2.seq | E3.seq |
|------|-------|--------|--------|--------|
| t0   | Insert E1, E2, E3 | 100 | 101 | 102 |
| t1   | Publisher fetches, E1 send fails | 100 | 101 | 102 |
| t2   | `IncrementRetryCount` on E1 | 100 | 101 | 102 |
| t3   | Publisher fetches | → E1, E2, E3 |

`sequence_number` is **not touched** by the retry UPDATE. Ordering is preserved. This is why sequence_number is the right column and rowversion is the wrong one.

---

## What `event_datetime_utc` / `event_ordinal` actually buy you as sort keys

Exactly one capability: **the caller's ability to declare a delivery order different from the insert order**. Concrete use cases:

1. **Backfill / replay of historical events.** You're importing a year of old events and want them delivered in the order the events originally occurred, not the order your replay loop inserts them.
2. **Multi-threaded producer with wall-clock ordering semantics.** Multiple worker threads insert rows as they arrive, but downstream consumers expect delivery ordered by the event's emission time, not its insert time.
3. **Same-transaction reordering for careless callers.** A saga builds a set of events in whatever order the control flow dictates, then flushes them. `event_ordinal` lets the caller explicitly stamp causal order without rearranging the insert sequence.

If none of these apply to your deployment — if your callers always insert in the order they want delivery — then the **sort key role** of `event_datetime_utc` / `event_ordinal` is dead weight.

Conversely, if #1 or #2 is a hard requirement (replay / backfill with custom ordering is a first-class feature), their sort key role is load-bearing and must stay.

### The columns can stay even if they're dropped from the sort

Removing `event_datetime_utc` from the `ORDER BY` does **not** require dropping it from the schema. The column still has value as a **debugging / observability field**:

- When investigating "why was this message delivered so late?", knowing the caller-asserted event time (as distinct from the insert time / `created_at_utc`) is often the difference between a five-minute triage and an hour of log spelunking.
- Dead-letter forensics benefit from carrying the original business timestamp through to the dead letter table.
- Consumers that index or dedupe by business time need the column on the wire, independent of how the outbox table orders its fetches.

So the realistic simplification is: **keep the column, stop sorting on it.** `event_ordinal`, on the other hand, exists *only* as a tiebreaker for `event_datetime_utc`; once the sort doesn't reference `event_datetime_utc`, `event_ordinal` has no consumer and can be dropped outright (or kept inert if the cost of a schema migration outweighs the few bytes per row).

---

## Cost of the simplification

Even when the analysis supports dropping the columns, this is not a refactor. It is a schema + API break with non-trivial rollout cost.

### 1. Index rebuild

The current SQL Server `FetchBatch` does:

```sql
ORDER BY o.PartitionId, o.EventDateTimeUtc, o.EventOrdinal, o.SequenceNumber
```

Whatever clustered or covering index backs that query is keyed on this tuple. Switching to `ORDER BY PartitionId, SequenceNumber` requires dropping and rebuilding the index. On a production-sized table, that's a maintenance window and significant I/O. Same story on PostgreSQL with its `ORDER BY event_datetime_utc, event_ordinal, sequence_number`.

### 2. Schema migration

The minimal-cost path **keeps `event_datetime_utc` in the schema** as a debug/observability column and only drops it from the `ORDER BY`. In that case the schema migration is limited to `event_ordinal` (which has no purpose once `event_datetime_utc` is out of the sort). If even that is not worth a migration, `event_ordinal` can be left as a nullable/unused column and swept up in a future cleanup.

Regardless of whether columns are dropped, the flip of the `ORDER BY` itself is the risky part. A safe rollout looks like:

1. Deploy new publisher binaries that **still write** both columns but **sort by `sequence_number` only**. Verify in production for a bake period.
2. Stop all publishers (brief downtime) and confirm the backlog is drained or acceptably redelivered.
3. *(Optional)* Drop `event_ordinal` and/or stop writing it from callers in a follow-up.
4. *(Optional)* Keep or drop `event_datetime_utc` based on whether the debug/forensics value is worth the column cost.

Step 2 is the critical one. If you flip the `ORDER BY` while rows inserted under the old contract (where callers may have relied on `event_datetime_utc != insert order`) are still in the table, those rows get delivered in the wrong order. Drain-before-flip is the only safe approach.

### 3. Caller API impact

If `event_datetime_utc` stays in the schema as a debug column, callers can keep writing it unchanged — no API break, just a quiet semantics shift (the column no longer influences delivery order). `event_ordinal` is different: if it's dropped from the schema, every producer's `INSERT` that references it must be updated. Options:

- Keep `event_ordinal` as a nullable / ignored column for a deprecation window before dropping.
- Coordinate a flag-day across services.
- Leave it in place indefinitely as a no-op column. Cheapest, ugliest.

### 4. Loss of the replay/backfill escape hatch

Regardless of whether `event_datetime_utc` is dropped from the schema, **dropping it from the sort** removes the ability to have the outbox deliver messages in an order other than insert order. If you ever need use case #1 or #2 above after the flip, the options are:

- Revert the `ORDER BY` change (cheap if the column was kept in the schema for debug purposes).
- Stage replays through a separate topic with its own ordering rules.
- Insert backfill rows in exactly the order you want them delivered (single-threaded replay).

For a greenfield library this is fine. For an established deployment with heterogeneous producers, it's a real loss of flexibility. The up-side of keeping `event_datetime_utc` in the schema as a debug column is that the revert path is a one-line SQL change, not a schema migration.

### 5. Documentation churn

`docs/outbox-requirements-invariants.md` defines the ordering contract in terms of `event_datetime_utc, event_ordinal, sequence_number`. This is the document the `CLAUDE.md` review checklist points reviewers at. Flipping the contract means revising the invariants doc, the `publisher-flow.md`, the `architecture.md`, the review checklist itself, and the per-store READMEs. Any one of those missed becomes a trap for a future reviewer applying the old rule to the new code.

---

## Recommendation

**Do not switch the sort key as part of an incidental refactor.** The analysis is correct: under the "callers insert in order" stipulation, `event_datetime_utc` and `event_ordinal` are redundant with `sequence_number` + the ceiling filter. But the change is:

- Externally visible (caller API, schema).
- Removes a documented escape hatch (replay with custom ordering).
- Requires index rebuilds on production-sized tables.
- Touches the invariants document that the review checklist is anchored on.
- Needs a staged rollout with a backlog drain.

This deserves its own RFC, a consumer survey ("is anyone relying on event_datetime_utc != insert order?"), and a coordinated migration plan — not a drive-by commit.

**Do not substitute `rowversion` for the sort column under any circumstances.** Retry updates bump rowversion, which pushes the failing message to the back of its partition's queue and corrupts ordering. This is a critical bug. If the topic comes up again, this document exists so the reasoning doesn't have to be rediscovered.

**If the simplification is pursued, simplify to `sequence_number`, not `rowversion`/`xmin`.** And when you do:

1. Keep the ceiling filter. It is doing independent work and is not affected by this change.
2. Remove `event_datetime_utc` and `event_ordinal` from the `ORDER BY` and from the ordering contract in `outbox-requirements-invariants.md`.
3. **Keep `event_datetime_utc` in the schema** as a debug / observability / dead-letter forensics column. It no longer influences delivery order, but it stays on the row and on the wire. This also preserves a cheap revert path if use cases #1 or #2 ever surface later.
4. `event_ordinal` has no purpose once `event_datetime_utc` is out of the sort — drop it from the schema and the caller API, or leave it nullable/inert if a migration isn't worth the churn.
5. Rebuild the backing index on `(partition_id, sequence_number)`.
6. Drain the backlog before flipping the `ORDER BY`.

---

## Summary table

| Candidate sort | Immutable after retry | Monotonic at insert | Uniquely orders within txn | Verdict |
|----------------|----------------------|---------------------|----------------------------|---------|
| `event_datetime_utc, event_ordinal, sequence_number` (current) | Yes | Caller-asserted | Yes (via `event_ordinal`) | Works; supports replay |
| `sequence_number` alone (with `event_datetime_utc` kept as debug column) | **Yes** | **Yes** | **Yes** | Works if callers insert in order; keeps forensics value |
| SQL Server `rowversion` | **No** — retry bumps it | Yes | Yes | **Broken** — reorders retried messages |
| PG `xmin` | Yes (on the live tuple) | Per-transaction, not per-row | **No** — needs tiebreaker | Awkward; asymmetric with SQL Server |
