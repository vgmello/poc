# Deep Technical Review — Core Logic, DB Queries, and Index Strategy

**Review date:** 2026-04-22  
**Reviewer:** GPT-5.3-Codex  
**Scope:** `src/Outbox.Core`, `src/Outbox.PostgreSQL`, `src/Outbox.SqlServer`, install scripts

---

## 1) Core engine logic assessment (`Outbox.Core`)

### What was checked
- Loop orchestration and restart policy in `OutboxPublisherService`.
- Circuit breaker interaction with retry/dead-letter paths.
- Health state transitions and restart counters.
- Publisher registration/unregistration lifecycle.

### Assessment
- The core loop model is robust for at-least-once semantics: linked CTS coordination, bounded restart policy, and clear escalation (`StopApplication`) after repeated instability are all production-grade patterns.
- Failure isolation boundaries are sound: transport/store exceptions remain inside loops and are surfaced via health + metrics instead of immediate process crash.
- Retry semantics are consistent with documented constraints (transient/non-transient split, bounded attempts before DLQ).

### Residual risk to monitor
- At-least-once duplicates remain intrinsic (expected by design); downstream idempotency remains mandatory.
- Restart-thrashing can still occur under persistent infrastructure faults; alerting on `ConsecutiveLoopRestarts` should be mandatory in production.

---

## 2) Query-path assessment

### PostgreSQL path
- `FetchBatch` uses runtime partition mapping via `hashtext(partition_key) % total_partitions` and visibility guard via `xmin < pg_snapshot_xmin(pg_current_snapshot())`, which correctly avoids in-flight rows.
- Rebalance/claim SQL uses `FOR UPDATE SKIP LOCKED`, which is correct for multi-publisher coordination.

### SQL Server path
- `FetchBatch` relies on persisted computed `PartitionId`, `NOLOCK`, and `MIN_ACTIVE_ROWVERSION()` combination.
- Rebalance and orphan-claim logic properly uses lock hints (`UPDLOCK`, `READPAST`) in candidate-selection CTEs.

### Assessment
- Query semantics align with outbox invariants (ordering, claim/release safety, stale-owner handover).
- The main opportunity was not query correctness but **index support for high-cardinality publisher/group environments**.

---

## 3) Index strategy and changes made in this patch

### Why this change
Outbox data-path indexing was already present for SQL Server message polling (`IX_Outbox_Pending`), but control-plane loops (heartbeat/rebalance/ownership lookups) relied mainly on PK scans. With many publisher groups and frequent loop execution, this can create avoidable CPU/io churn.

### Added indexes — PostgreSQL
- `ix_outbox_publishers_heartbeat (outbox_table_name, last_heartbeat_utc)` — covers the active-publisher COUNTs and the `NOT IN` subquery in `Rebalance` / `ClaimOrphanPartitions` (`PostgreSqlQueries.cs:132-137, 142-144, 176-178, 211-213`).
- `ix_outbox_partitions_owner (outbox_table_name, owner_publisher_id, partition_id)` — covers `GetOwnedPartitions`, the partitions `Heartbeat` UPDATE, `UnregisterPublisher` UPDATE, the `currently_owned` COUNTs inside `Rebalance`, and per-owner scans within rebalance/claim.

### Added indexes — SQL Server
- `IX_OutboxPublishers_Heartbeat (OutboxTableName, LastHeartbeatUtc) INCLUDE (PublisherId)` — same role as above; `INCLUDE (PublisherId)` makes the `NOT IN` subquery index-only.
- `IX_OutboxPartitions_Owner (OutboxTableName, OwnerPublisherId, PartitionId) INCLUDE (OwnedSinceUtc, GraceExpiresUtc)` — same role as above; the `INCLUDE` columns make the `FetchBatch` subquery at `SqlServerQueries.cs:74-80` fully covering, removing key lookups on the hottest query in the system.

### Considered and rejected

A filtered `(outbox_table_name, grace_expires_utc, partition_id) WHERE grace_expires_utc IS NOT NULL` index was considered and **not added**. It cannot serve the two queries that want grace state most — `FetchBatch` and the `Rebalance` mark-stale UPDATE both require `GraceExpiresUtc IS NULL`, which a `WHERE GraceExpiresUtc IS NOT NULL` predicate excludes by definition. The partitions `Heartbeat` UPDATE leads on a specific `OwnerPublisherId`, where the owner index reaches fair-share rows directly instead of scanning every grace-flagged row in the group. That leaves only the expired-grace branch of the claim CTE in `Rebalance` as a unique beneficiary — a rare, transient state on ≤ 64 rows per group. The write cost (every heartbeat that clears a grace window moves a row out of the filtered index) outweighs that narrow win, and on SQL Server the `INCLUDE` would have duplicated columns already covered by the owner index.

### Expected impact
- Lower latency variance in heartbeat/rebalance/orphan-sweep loops as publisher/group count increases.
- Reduced scan pressure on partition/publisher infrastructure tables.
- On SQL Server, `FetchBatch`'s partitions subquery becomes index-only via the owner index's `INCLUDE (GraceExpiresUtc)`.
- No behavior change to message ordering or delivery guarantees.

### Validation still owed

The "lower latency variance" claim has not been measured. Before trusting it in production, capture `EXPLAIN (ANALYZE, BUFFERS)` (PostgreSQL) and `SET STATISTICS IO, TIME ON` (SQL Server) for `FetchBatch`, `Rebalance`, and `Heartbeat` at realistic group/publisher cardinality, before and after. The SQL Server `FetchBatch` subquery is the most load-bearing case to confirm.

---

## 4) Production recommendation

With these index changes, the solution is better positioned for production scale in environments with multiple publisher instances and groups. Before final rollout, validate with:

1. Query plans for rebalance and orphan-claim in your target DB versions.
2. Load test with realistic publisher-group cardinality.
3. Alerts on stale heartbeat, loop restarts, DLQ growth, and pending backlog trend.

