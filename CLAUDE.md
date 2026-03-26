# Outbox Library — Project Context

## What is this?

A .NET outbox pattern library with pluggable transports (Kafka, EventHub) and stores (PostgreSQL, SQL Server). Guarantees at-least-once delivery with partition-based work distribution across multiple publisher instances.

## Key Architecture

- `OutboxPublisherService` (BackgroundService) runs 5 parallel loops: publish, heartbeat, rebalance, orphan sweep, dead-letter sweep
- Messages are partitioned by `hash(partition_key) % total_partitions`, each partition owned by one publisher
- Circuit breaker per topic prevents retry-count burn during broker outages
- Leases with timeout prevent duplicate processing across publishers

## Critical Documents

- **Requirements and invariants:** `docs/outbox-requirements-invariants.md` — READ THIS before making any changes. Contains all behavioral invariants, transport/store contracts, and anti-patterns.
- **Known limitations:** `docs/known-limitations.md` — Documents transport-specific issues and design trade-offs.
- **Failure scenarios:** `docs/failure-scenarios-and-integration-tests.md` — All 14 failure scenarios with expected behavior.

## Testing

- Unit tests: `tests/Outbox.Core.Tests/`, `tests/Outbox.Kafka.Tests/`, `tests/Outbox.EventHub.Tests/`, `tests/Outbox.Store.Tests/` (fast, no infrastructure)
- Integration tests: `tests/Outbox.IntegrationTests/` (requires Docker for Testcontainers)
- Run unit tests: `dotnet test tests/Outbox.Core.Tests/ && dotnet test tests/Outbox.Kafka.Tests/ && dotnet test tests/Outbox.EventHub.Tests/ && dotnet test tests/Outbox.Store.Tests/`
- Run integration tests: `dotnet test tests/Outbox.IntegrationTests/` (takes ~3 minutes)
- Run all: `dotnet test src/Outbox.slnx`

## Review Checklist

Before approving any change, verify against `docs/outbox-requirements-invariants.md`:
- [ ] Retry count only incremented on transport failure (never on delete failure or circuit-open release)
- [ ] Lease owner checked on all delete/release/dead-letter operations
- [ ] `CancellationToken.None` used for cleanup operations in failure paths
- [ ] No tight loops without backoff (check circuit-open and error paths)
- [ ] Health state updated before event handler callbacks
- [ ] Transports don't dispose DI singletons
- [ ] Sub-batch splitting respects `MaxBatchSizeBytes`
