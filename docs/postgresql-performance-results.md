# PostgreSQL Performance Test Results

**Date:** 2026-04-07
**Platform:** Linux 6.17.0 (ARM64), .NET 10.0
**Database:** PostgreSQL 16 Alpine via Testcontainers
**Transports:** Redpanda v24.2.18, EventHub emulator (latest, 32 partitions)
**Partitions:** 64, Publisher threads: 4

---

## Bulk Throughput

Pre-seeded messages drained to zero by the publisher(s).

| Transport | Publishers | Messages  | Duration | Msg/sec | Poll p50 | Poll p95 | Pub p50 | Pub p95 |
|-----------|------------|-----------|----------|---------|----------|----------|---------|---------|
| Redpanda  | 1          | 1,000,000 | 4:01     | 4,141   | 9.1ms    | 14.9ms   | 10.5ms  | 21.4ms  |
| Redpanda  | 2          | 1,000,000 | 2:00     | 8,321   | 9.0ms    | 163.2ms  | 10.3ms  | 14.8ms  |
| Redpanda  | 4          | 1,000,000 | 0:59     | 16,761  | 18.9ms   | 157.7ms  | 10.3ms  | 20.4ms  |
| EventHub  | 1          | 200,000   | 3:10     | 1,052   | 2.1ms    | 37.9ms   | 2.8ms   | 6.4ms   |
| EventHub  | 2          | 200,000   | 2:24     | 1,383   | 5.4ms    | 49.1ms   | 2.2ms   | 5.0ms   |
| EventHub  | 4          | 200,000   | 2:34     | 1,292   | 29.2ms   | 96.5ms   | 2.7ms   | 6.9ms   |

### Observations

- **Redpanda throughput scales near-linearly:** 1P (4,141/s) → 2P (8,321/s) → 4P (16,761/s) — 2.0x and 4.0x improvement respectively.
- **Redpanda 4P at 16,761/s** — highest throughput across all store/transport combinations.
- **Poll latency is excellent:** p50 of 9-19ms shows PostgreSQL's MVCC model handles the FetchBatch query efficiently with no lock contention.
- **EventHub throughput improved** with 32 emulator partitions: 1P now reaches 1,052/s (up from 673/s with 8 partitions). 2P peaks at 1,383/s.
- **EventHub 4P shows diminishing returns** (1,292/s vs 2P's 1,383/s) — emulator AMQP throughput saturates while poll latency increases.

---

## Sustained Load

Continuous message insertion at the target rate for 5 minutes.

| Transport | Publishers | Target Rate | Drain Rate | Peak Pending | Final Pending | Kept Up? |
|-----------|------------|-------------|------------|--------------|---------------|----------|
| Redpanda  | 1          | 1,000/s     | 1,000/s    | 5,000        | 0             | Yes      |
| Redpanda  | 2          | 1,000/s     | 1,000/s    | 188          | 0             | Yes      |
| Redpanda  | 4          | 1,000/s     | 1,000/s    | 140          | 0             | Yes      |
| EventHub  | 1          | 500/s       | 500/s      | 2,500        | 0             | Yes      |
| EventHub  | 2          | 500/s       | 500/s      | 100          | 0             | Yes      |
| EventHub  | 4          | 500/s       | 500/s      | 138          | 0             | Yes      |

### Observations

- **All combinations kept up** with their target ingestion rate — final pending count was 0 across the board.
- **1P has a noticeable startup backlog:** 5,000 pending for Redpanda at 1K/s, 2,500 for EventHub at 500/s. This is the initial burst while the publisher starts up, rebalances, and begins draining.
- **2P+ dramatically reduces cursor distance:** Peak pending drops from 5,000 to 188 (Redpanda) and from 2,500 to 100 (EventHub). Horizontal scaling is highly effective for keeping the pending backlog tight.
- **Redpanda at 1K/sec is well within capacity.** Based on bulk throughput (16,761/s max at 4P), PostgreSQL+Redpanda has **16x headroom**.

---

## Comparison with SQL Server

| Metric | PostgreSQL | SQL Server (Azure SQL Edge ARM) | Ratio |
|--------|-----------|--------------------------------|-------|
| Bulk throughput (1P, Redpanda) | 4,141 msg/s | 6,272 msg/s | 0.7x |
| Bulk throughput (4P, Redpanda) | 16,761 msg/s | 13,181 msg/s | 1.3x |
| Poll latency p50 (1P) | 9.1ms | 17.7ms | 1.9x faster |
| Poll latency p95 (1P) | 14.9ms | 43.4ms | 2.9x faster |
| EventHub bulk (1P) | 1,052 msg/s | 376 msg/s | 2.8x |

PostgreSQL shows stronger scaling at higher publisher counts (4P: 16,761 vs 13,181 msg/s) thanks to MVCC's lower lock contention. SQL Server edges ahead at 1P due to efficient single-connection query plan caching. Both stores comfortably exceed typical production workloads.
