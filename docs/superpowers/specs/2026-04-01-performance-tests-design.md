# Performance Test Suite Design

## Overview

A new xUnit + Testcontainers performance test project that measures publisher throughput, sustained load handling, and horizontal scaling across all store+transport combinations.

## Goals

1. **Bulk throughput** — How long to drain 1M pre-seeded messages through the full pipeline (store fetch + transport publish + store delete)?
2. **Sustained load** — Can publishers keep up with 1K messages/second continuous ingestion? How does the pending backlog behave over time?
3. **Horizontal scaling** — How does throughput scale with 1, 2, and 4 concurrent publisher instances?

## Project Structure

```
tests/Outbox.PerformanceTests/
  Outbox.PerformanceTests.csproj
  Fixtures/
    PerformanceFixture.cs          # Testcontainers: SQL Server, PostgreSQL, Redpanda, EventHub emulator
  Helpers/
    MessageProducer.cs             # Bulk seeding + sustained-rate insertion
    MetricsCollector.cs            # MeterListener on OutboxInstrumentation
    PerfReportWriter.cs            # Markdown report generator
    PerfTestOptions.cs             # Publisher options tuned for throughput
    TestMatrix.cs                  # Store+Transport+PublisherCount combinations
  Scenarios/
    BulkThroughputTests.cs         # 1M message drain tests
    SustainedLoadTests.cs          # 1K msg/sec sustained insertion tests
  reports/                         # Generated markdown reports (gitignored)
```

## Infrastructure

Reuses the Testcontainers pattern from `tests/Outbox.IntegrationTests/`:

- **SQL Server:** Azure SQL Edge container
- **PostgreSQL:** PostgreSQL container
- **Redpanda:** Redpanda container (Kafka-compatible)
- **EventHub:** EventHub emulator container

All containers shared via xUnit `IAsyncLifetime` fixture and `[CollectionDefinition]`. Schema initialized from existing `db_scripts/install.sql`.

## Test Matrix

Each scenario is parameterized via `[Theory]` + `[MemberData]`:

| Store      | Transport | Publishers |
|------------|-----------|------------|
| SQL Server | Redpanda  | 1, 2, 4   |
| SQL Server | EventHub  | 1, 2, 4   |
| PostgreSQL | Redpanda  | 1, 2, 4   |
| PostgreSQL | EventHub  | 1, 2, 4   |

12 runs per scenario, 24 total.

## Scenario 1: Bulk Throughput

1. Pre-seed 1M messages via bulk SQL insert (10K per batch)
2. Messages distributed across 10 partition keys and 3 topics
3. Start N publisher instances with `PerfTestOptions`
4. Wait until `messages.pending` gauge hits 0
5. Capture: total duration, messages/sec, poll duration p50/p95/p99, publish duration p50/p95/p99, batch size distribution

## Scenario 2: Sustained Load

1. Start N publisher instances with `PerfTestOptions`
2. Start `MessageProducer` inserting at 1K msg/sec (100 messages every 100ms, drift-corrected)
3. Messages distributed across 10 partition keys and 3 topics
4. Run for 5 minutes
5. Sample `messages.pending` gauge every 5 seconds
6. Capture: pending count over time, average drain rate, peak pending, final pending, whether publishers kept up, publish/poll latency percentiles

## Components

### MessageProducer

Inserts messages into the outbox table at a configurable rate.

**Bulk mode:** Single-statement multi-row inserts, 10K rows per batch, for fast pre-seeding.

**Sustained mode:** 100 messages every 100ms using `Stopwatch` + `Task.Delay` with drift correction to maintain target rate. Runs until cancelled.

**Message distribution:** 10 distinct partition keys (`pk-0` through `pk-9`), 3 topics (`perf-topic-0` through `perf-topic-2`). Payload is a fixed ~500-byte JSON blob.

### MetricsCollector

Uses `System.Diagnostics.Metrics.MeterListener` to subscribe to the existing `OutboxInstrumentation` meter:

- **Counters:** `outbox.messages.published`, `outbox.publish.failures`, `outbox.messages.dead_lettered`
- **Histograms:** `outbox.publish.duration`, `outbox.poll.duration`, `outbox.poll.batch_size`
- **Gauge snapshots:** `outbox.messages.pending` sampled at configurable interval (default 5s)

Stores raw histogram values in-memory. Provides methods for percentile calculation (p50, p95, p99) and counter totals.

### PerfReportWriter

Generates a markdown file at `tests/Outbox.PerformanceTests/reports/{timestamp}-perf-report.md`.

**Report sections:**
- Environment info (OS, CPU, RAM, .NET version, container versions)
- Bulk throughput results table (Store, Transport, Publishers, Messages, Duration, Msg/sec, Poll p50/p95, Pub p50/p95)
- Sustained load results table (Store, Transport, Publishers, Target Rate, Avg Drain Rate, Peak Pending, Final Pending, Kept Up?)
- Pending count over time table for sustained load (sampled every 5s)

Also prints summary tables to console during test execution.

### PerfTestOptions

Publisher options tuned for throughput:

```
BatchSize = 500
MinPollIntervalMs = 50
MaxPollIntervalMs = 1000
HeartbeatIntervalMs = 5000
HeartbeatTimeoutSeconds = 30
PartitionGracePeriodSeconds = 10
RebalanceIntervalMs = 5000
OrphanSweepIntervalMs = 5000
DeadLetterSweepIntervalMs = 60000
CircuitBreakerFailureThreshold = 3
CircuitBreakerOpenDurationSeconds = 30
PublishThreadCount = 4
```

### Multi-Publisher Setup

Each publisher runs in its own `IHost` instance within the same process. Each gets an independent `publisherId` via `RegisterPublisherAsync`. The partition rebalance loop distributes work automatically. A short stabilization period (15s) is allowed after startup for rebalancing to settle before measurements begin.

## Reporting Format

### Console Output

Live progress during tests:
```
[Bulk] SqlServer+Redpanda 1P: 250,000/1,000,000 published (25%) — 3,412 msg/sec
[Bulk] SqlServer+Redpanda 1P: 500,000/1,000,000 published (50%) — 3,501 msg/sec
```

Summary table at completion.

### Markdown Report

```markdown
# Performance Test Report — 2026-04-01T14:30:00Z

## Environment
- OS: Linux 6.17.0
- .NET: 9.0.x
- CPU: ...
- RAM: ...

## Bulk Throughput
| Store | Transport | Publishers | Messages | Duration | Msg/sec | Poll p50 | Poll p95 | Pub p50 | Pub p95 |
|-------|-----------|------------|----------|----------|---------|----------|----------|---------|---------|
| ... | ... | ... | ... | ... | ... | ... | ... | ... | ... |

## Sustained Load
| Store | Transport | Publishers | Target | Drain Rate | Peak Pending | Final Pending | Kept Up? |
|-------|-----------|------------|--------|------------|--------------|---------------|----------|
| ... | ... | ... | ... | ... | ... | ... | ... |

## Pending Over Time (Sustained)
| Time | SqlServer+Redpanda 1P | SqlServer+Redpanda 2P | ... |
|------|----------------------|----------------------|-----|
| 0s   | 0 | 0 | ... |
| 5s   | 230 | 120 | ... |
```

## Test Execution

```bash
# Run all performance tests
dotnet test tests/Outbox.PerformanceTests/

# Run only bulk throughput
dotnet test tests/Outbox.PerformanceTests/ --filter "FullyQualifiedName~BulkThroughput"

# Run only sustained load
dotnet test tests/Outbox.PerformanceTests/ --filter "FullyQualifiedName~SustainedLoad"
```

Expected runtime: ~30-45 minutes for the full suite (dominated by the 5-minute sustained load tests x 12 combinations).

## Out of Scope

- BenchmarkDotNet statistical analysis (can be added later)
- Fake/in-memory transports for isolated benchmarking
- CI integration (manual runs only for now)
- Grafana/OpenTelemetry export
