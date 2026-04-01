# SQL Server Performance Test Results

**Date:** 2026-04-01
**Platform:** Linux 6.17.0 (ARM64), .NET 10.0
**Database:** Azure SQL Edge (mcr.microsoft.com/azure-sql-edge:latest) via Testcontainers
**Transports:** Redpanda v24.2.18, EventHub emulator (latest)
**Partitions:** 64, Publisher threads: 4

---

## Bulk Throughput

Pre-seeded messages drained to zero by the publisher(s).

| Transport | Publishers | Messages | Duration | Msg/sec | Poll p50 | Poll p95 | Pub p50 | Pub p95 |
|-----------|------------|----------|----------|---------|----------|----------|---------|---------|
| Redpanda  | 1          | 100,000  | 1:15     | 1,330   | 140.3ms  | 206.2ms  | 10.2ms  | 20.1ms  |
| Redpanda  | 2          | 100,000  | 0:50     | 1,976   | 107.1ms  | 250.9ms  | 10.3ms  | 20.3ms  |
| Redpanda  | 4          | 100,000  | 0:56     | 1,758   | 114.8ms  | 319.3ms  | 10.3ms  | 20.9ms  |
| EventHub  | 1          | 50,000   | 1:12     | 687     | 119.8ms  | 210.8ms  | 5.3ms   | 16.9ms  |
| EventHub  | 2          | 50,000   | 1:45     | 474     | 40.3ms   | 160.2ms  | 6.1ms   | 19.6ms  |
| EventHub  | 4          | 50,000   | 1:05     | 765     | 31.8ms   | 154.6ms  | 6.6ms   | 36.0ms  |

### Observations

- **Redpanda throughput scales with publishers:** 1P (1,330/s) to 2P (1,976/s) shows ~49% improvement. 4P (1,758/s) regresses slightly due to partition contention on Azure SQL Edge ARM.
- **Poll latency dominates:** p50 is 107-140ms for Redpanda, compared to 3-5ms on PostgreSQL. The `CHECKSUM()` hash computation per row and `MIN_ACTIVE_ROWVERSION()` filter are the main costs.
- **EventHub emulator caps around 475-765 msg/sec** — the emulator's AMQP throughput is the bottleneck, not SQL Server.
- **Publish latency (transport send) is fast:** Redpanda p50 ~10ms, EventHub p50 ~5ms. The DB fetch is the bottleneck, not the broker.

---

## Sustained Load

Continuous message insertion at the target rate for 5 minutes.

| Transport | Publishers | Target Rate | Drain Rate | Peak Pending | Final Pending | Kept Up? |
|-----------|------------|-------------|------------|--------------|---------------|----------|
| Redpanda  | 1          | 100/s       | 100/s      | 510          | 0             | Yes      |
| Redpanda  | 2          | 100/s       | 100/s      | 16           | 0             | Yes      |
| Redpanda  | 4          | 100/s       | 100/s      | 16           | 0             | Yes      |
| EventHub  | 1          | 50/s        | 50/s       | 250          | 0             | Yes      |
| EventHub  | 2          | 50/s        | 50/s       | 8            | 0             | Yes      |
| EventHub  | 4          | 50/s        | 50/s       | 7            | 0             | Yes      |

### Observations

- **All combinations kept up** with their target ingestion rate — final pending count was 0 across the board.
- **1P shows initial backlog spike** (510 pending for Redpanda, 250 for EventHub) as the publisher starts up and takes time to ramp. With 2P+, the peak drops to 7-16 — horizontal scaling effectively eliminates cursor distance.
- **Redpanda handles 100 msg/sec comfortably.** Based on bulk throughput (1,330/s max), SQL Server could sustain ~1,000/s with Redpanda, but the sustained test used a conservative 100/s target.
- **EventHub emulator at 50/s is well within capacity** (bulk max ~765/s).

---

## Key Bottleneck: FetchBatch Query

The dominant performance factor is the SQL Server `FetchBatchAsync` query, with p50 latency of 107-140ms vs PostgreSQL's 3-5ms. Root causes:

1. **Per-row `CHECKSUM()` computation** — The partition hash `ABS(CAST(CHECKSUM(o.PartitionKey) AS BIGINT)) % @TotalPartitions` is evaluated on every candidate row in the JOIN condition. SQL Server cannot push this into an index seek.
2. **`MIN_ACTIVE_ROWVERSION()` snapshot filter** — Adds overhead for version visibility checking on every candidate row.
3. **Azure SQL Edge on ARM** — The ARM build of SQL Server has weaker query optimization and lower single-core throughput than x86 SQL Server.

The query uses no row-level lock hints (partition ownership is the sole isolation mechanism). The index `IX_Outbox_Pending` is a covering index including `Headers`, `Payload`, and `PayloadContentType` to eliminate key lookups.

### Potential improvements (not yet implemented)

- **Computed persisted column** for `PartitionId` to avoid per-row CHECKSUM
- **Pre-filtered subquery** with `IN (owned partition IDs)` instead of JOIN
- **Test on x86 SQL Server** to isolate ARM-specific overhead
