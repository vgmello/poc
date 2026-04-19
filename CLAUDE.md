# Outbox Library — Project Context

## What is this?

A .NET outbox pattern library with pluggable transports (Kafka, EventHub) and stores (PostgreSQL, SQL Server). Guarantees at-least-once delivery with partition-based work distribution across multiple publisher instances.

## Key Architecture

- `OutboxPublisherService` (BackgroundService) runs 4 parallel loops: publish, heartbeat, rebalance, orphan sweep
- Messages are partitioned by `hash(partition_key) % total_partitions`, each partition owned by one publisher
- Circuit breaker per topic prevents attempt-counter burn during broker outages; dead-lettering happens inline within the publish loop
- Partition ownership prevents duplicate processing across publishers

## Critical Documents

- **Requirements and invariants:** `docs/outbox-requirements-invariants.md` — READ THIS before making any changes. Contains all behavioral invariants, transport/store contracts, and anti-patterns.
- **Known limitations:** `docs/known-limitations.md` — Documents transport-specific issues and design trade-offs.
- **Failure scenarios:** `docs/failure-scenarios-and-integration-tests.md` — All 14 failure scenarios with expected behavior.

## Testing

- Unit tests: `tests/Outbox.Core.Tests/`, `tests/Outbox.Kafka.Tests/`, `tests/Outbox.EventHub.Tests/`, `tests/Outbox.Store.Tests/` (fast, no infrastructure)
- Integration tests: `tests/Outbox.IntegrationTests/` (requires Docker for Testcontainers)
- Run unit tests: `dotnet test tests/Outbox.Core.Tests/ && dotnet test tests/Outbox.Kafka.Tests/ && dotnet test tests/Outbox.EventHub.Tests/ && dotnet test tests/Outbox.Store.Tests/`
- Run integration tests: `dotnet test tests/Outbox.IntegrationTests/` (takes ~3 minutes)
- Run only EventHub integration tests: `dotnet test tests/Outbox.IntegrationTests/ --filter "FullyQualifiedName~EventHub"`
- Run only Kafka integration tests: `dotnet test tests/Outbox.IntegrationTests/ --filter "FullyQualifiedName!~EventHub"`
- Run performance tests: `dotnet test tests/Outbox.PerformanceTests/` (takes ~60 minutes, requires Docker)
- Run all: `dotnet test src/Outbox.slnx`

## Code and doc conventions

- **This project is pre-release. No migration paths, no backward-compatibility shims.** When code or schema is deleted, it is gone — do not leave comments explaining what used to be there, do not retain `// removed`/`-- No longer needed` markers, do not write SQL comments justifying the absence of dropped indexes/columns. A fresh reader needs to understand current state, not a changelog.
- Keep `install.sql` minimal: create what exists, nothing more. Design rationale belongs in design docs (`docs/`), not SQL comments.
- When updating docs that described removed code, rewrite the passage to describe current state — don't frame it as "X was removed because Y."

## Git workflow

- Feature branches MUST be merged into `main` via **squash merge**. Never fast-forward, never create a merge commit.
- The squash commit message must be substantial: motivation, behavioral model, API/schema changes, test status, and any out-of-scope manual follow-ups.
- After the squash merge, force-delete the feature branch (`git branch -D <branch>`); the per-task commits are intentionally discarded from `main`'s history.
- Do NOT push to `origin` without explicit instruction. The user owns when changes leave the local clone.

## Review Checklist

Before approving any change, verify against `docs/outbox-requirements-invariants.md`:
- [ ] **MESSAGE ORDERING MUST NEVER BE CORRUPTED.** Per-(topic, partitionKey) ordering is the core guarantee. Any change that could cause two publishers to process the same partition key simultaneously, or reorder messages within a partition key, is a critical bug. This includes: changing partition counts while publishers are running, changing the hash function, modifying the FetchBatch `ORDER BY sequence_number` clause, breaking the "callers insert in delivery order" stipulation, or breaking the single-writer-per-partition invariant.
- [ ] Attempt counter only incremented on **non-transient** transport failure; transient failures record circuit failures instead
- [ ] DLQ never happens while the circuit is open — the retry loop must exit via `CircuitOpened`, not via the DLQ branch
- [ ] No per-message lease columns remain (partition ownership is the sole isolation mechanism)
- [ ] `CancellationToken.None` used for cleanup operations in failure paths
- [ ] No tight loops without backoff (check circuit-open and error paths)
- [ ] Health state updated before event handler callbacks
- [ ] Transports don't dispose DI singletons
- [ ] Sub-batch splitting respects `MaxBatchSizeBytes`
- [ ] FetchBatch query uses no row-level lock hints (partition ownership is the sole isolation mechanism, not ROWLOCK/READPAST)
