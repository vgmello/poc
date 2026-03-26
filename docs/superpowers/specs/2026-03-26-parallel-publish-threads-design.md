# Parallel Publish Threads Design

## Problem

Today each publisher instance runs a single publish loop (`PublishLoopAsync`) that processes all its owned partitions sequentially. With 64 partitions and, say, 2 publishers, each publisher owns ~32 partitions but processes them through a single poll-and-publish cycle. This limits throughput — the transport round-trip for one partition key blocks processing of all others.

## Goal

Allow configurable parallelism **within** a publisher instance so that multiple threads can concurrently process different partitions from the same leased batch. Ordering within a partition key must be preserved.

## Design

### Configuration

New option in `OutboxPublisherOptions`:

```csharp
public int PublishThreadCount { get; set; } = 4;
```

- Default: 4
- Validation: must be >= 1
- When set to 1, behavior is identical to the current single-loop design

### Architecture: Coordinator + N Workers

The current `PublishLoopAsync` is refactored into a coordinator and N worker tasks.

**Coordinator (single loop, replaces current `PublishLoopAsync`):**

1. Polls `LeaseBatchAsync` — unchanged, fetches up to `BatchSize` messages across all owned partitions
2. If batch is empty — adaptive backoff (same as today)
3. Separates poison messages (`RetryCount >= MaxRetryCount`) and dead-letters them immediately (same as today)
4. Groups healthy messages by `(TopicName, PartitionKey)` — same as today
5. Assigns each group to a worker using `partition_id % PublishThreadCount`, where `partition_id = hash(partition_key) % total_partitions`
6. Launches N workers concurrently via `Task.WhenAll`
7. Runs the finally block to release any unprocessed sequences (same safety net as today)

**Each worker (runs concurrently):**

- Receives its assigned message groups
- Iterates each group sequentially: circuit breaker check, `SendAsync`, delete/release
- Uses the same logic as today's inner loop — full success, full failure, partial failure paths are unchanged
- Shares the circuit breaker instance and instrumentation counters with other workers

**Housekeeping loops unchanged:**

The 4 non-publish loops (heartbeat, rebalance, orphan sweep, dead-letter sweep) remain single-instance per publisher. Only the publish loop is parallelized.

### Partition-to-Worker Assignment

Assignment is deterministic:

```
worker_index = (hash(partition_key) % total_partitions) % PublishThreadCount
```

Properties:
- Same partition key always goes to the same worker within a poll cycle
- Ordering within a partition key is preserved (single worker, sequential processing)
- Different partition keys can be processed concurrently across workers
- No coordination needed between workers — assignment is purely derived from the messages in hand

### Ordering Guarantee

Messages with the same `(TopicName, PartitionKey)` are always assigned to the same worker. Within that worker, they are processed in `EventDateTimeUtc + EventOrdinal` order (the existing sort). This preserves the causal ordering invariant.

### Thread Safety

**Already thread-safe (no changes needed):**

- `TopicCircuitBreaker` — uses `ConcurrentDictionary<string, TopicState>` internally
- `OutboxInstrumentation` — uses `System.Diagnostics.Metrics` counters (atomic by design)
- `IOutboxStore` — each call is an independent DB operation

**Changed for concurrency:**

- `unprocessedSequences` — changes from `HashSet<long>` to `ConcurrentDictionary<long, byte>`. Each worker removes its sequences as it processes them. Workers operate on disjoint sets of sequence numbers, so no contention occurs. The finally block releases whatever remains.

**No shared mutable state between workers:**

- Each worker gets its own subset of message groups (no overlap by partition)
- Transport `SendAsync` calls are independent per group
- Delete/release calls operate on disjoint sequence numbers

### Error Handling

**Worker-level failures:**

- Each worker handles its own transport errors exactly as today (full failure, partial failure, circuit-open release). No change to retry/release logic.
- If a worker throws an unexpected exception, the coordinator catches it after `Task.WhenAll`. Unfinalized sequences remain in `unprocessedSequences` and are released without retry in the finally block.

**Cancellation:**

- The coordinator passes its `CancellationToken` to all workers
- Workers check cancellation between groups (not mid-send)
- On cancellation, workers exit their loops, and the finally block cleans up with `CancellationToken.None`

**Circuit breaker interaction:**

- One worker's transport failure records against the shared circuit breaker
- If the circuit opens, other workers see it on their next group iteration and release remaining messages without retry
- This is desired behavior — a broker outage affects all workers immediately

No new error paths are introduced. The structural change is `Task.WhenAll` over N workers instead of a sequential `foreach` over groups.

### Batch Size Semantics

`BatchSize` (default 100) remains the total per poll, not per thread. Each thread gets roughly `BatchSize / PublishThreadCount` messages worth of work. Database load is unchanged.

## Changes

### Files to Modify

| File | Change |
|------|--------|
| `src/Outbox.Core/Options/OutboxPublisherOptions.cs` | Add `PublishThreadCount` property (default 4), validation >= 1 |
| `src/Outbox.Core/Engine/OutboxPublisherService.cs` | Refactor `PublishLoopAsync` into coordinator + N workers, `ConcurrentDictionary` for unprocessed sequences |

### Documentation Updates

| File | Change |
|------|--------|
| `docs/architecture.md` | Document parallel publish threads concept |
| `docs/getting-started.md` | Mention `PublishThreadCount` in scaling section |

### Tests

- Unit tests verifying partition-to-worker assignment distributes correctly
- Unit tests verifying `PublishThreadCount=1` preserves sequential behavior
- Existing integration tests run with default of 4 threads (validates no regressions)

### No Changes To

- `IOutboxStore` or any store implementations
- SQL queries
- Transport interface (`IOutboxTransport`)
- Circuit breaker (`TopicCircuitBreaker`)
- Rebalance/heartbeat/orphan sweep/dead-letter sweep loops
- Message model or partition table schema
