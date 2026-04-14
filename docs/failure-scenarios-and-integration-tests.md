# Outbox Library — Failure Scenarios & Integration Test Specifications

This document defines all failure/error scenarios that must be validated through integration tests, plus production runbook actions for each scenario. These tests require real database and broker infrastructure (not mocks).

---

## Test Infrastructure Requirements

- **PostgreSQL** (or SQL Server) with outbox schema installed
- **Kafka/Redpanda** (or EventHub emulator) as the broker
- **Multiple publisher instances** (for partition ownership tests)
- **Network simulation** (e.g., Toxiproxy or iptables rules for simulating broker/DB outages)
- **Test harness** that can insert messages into the outbox table directly and read from broker topics

---

## Scenario 1: Broker Down for Extended Period

**What we're testing:** When the broker (EventHub/Kafka) becomes unreachable, messages accumulate in the outbox table. The circuit breaker opens. When the broker recovers, the backlog drains automatically without data loss or ordering violations.

**Setup:**

1. Start a publisher instance connected to a real DB and broker
2. Insert 50 messages into the outbox table (mixed topics: `orders`, `shipments`)
3. Wait for publisher to drain all 50 messages (verify on consumer side)
4. Insert 100 more messages into the outbox table

**Test steps:**

1. **Block broker connectivity** (drop network to Kafka/EventHub via Toxiproxy or stop the broker container)
2. Wait for 3 consecutive publish failures → circuit breaker should open
3. **Assert:** `outbox.circuit_breaker.state_changes` metric incremented
4. **Assert:** `outbox.publish.failures` counter incremented by ≥3
5. **Assert:** Health check returns `Degraded` with open circuit breaker topics listed
6. Continue inserting 200 more messages over the next 60 seconds
7. **Assert:** Messages remain in outbox table (`SELECT COUNT(*) FROM outbox` = ~300)
8. **Assert:** Messages remain in the outbox without being dead-lettered (circuit breaker opens on transient failures before the in-memory attempt counter is exhausted)
9. Wait 30 seconds (circuit breaker `OpenDurationSeconds`)
10. **Assert:** Circuit transitions to HalfOpen, one probe batch is attempted, fails, re-opens
11. **Restore broker connectivity**
12. Wait for circuit to half-open and probe to succeed
13. **Assert:** Circuit closes, all ~300 messages eventually published
14. **Assert:** Consumer receives all messages, no duplicates within each partition key (order preserved)
15. **Assert:** Outbox table is empty after drain
16. **Assert:** Health check returns `Healthy`

**What can go wrong in production:**

- Backlog grows unbounded → monitor outbox table depth metric
- Burst of messages when broker recovers overwhelms downstream consumers
- Circuit breaker open duration too short → excessive probing against a recovering broker

**Runbook actions:**

- Monitor `outbox.messages.pending` gauge and `outbox.circuit_breaker.state_changes`
- Do NOT restart the publisher during outage — it's correctly backing off
- After recovery, consider temporarily increasing `BatchSize` if backlog is large
- Alert threshold: circuit open > 5 minutes

---

## Scenario 2: Database Down for Extended Period

**What we're testing:** When the database is unreachable, all store operations fail. The publisher retries with backoff. When DB recovers, the publisher resumes automatically, re-establishes partition ownership, and drains any pending messages.

**Setup:**

1. Start 2 publisher instances (A and B) against the same DB
2. Wait for partition rebalance — each should own ~16 partitions
3. Insert 100 messages distributed across multiple partition keys

**Test steps:**

1. Wait for both publishers to begin processing messages
2. **Stop the database** (stop PostgreSQL/SQL Server container)
3. **Assert:** All store operations fail with transient errors, logged at ERROR level
4. **Assert:** Heartbeat loop fails — publisher heartbeats become stale
5. **Assert:** Health check transitions to `Unhealthy` (stale heartbeat)
6. Wait 60 seconds (simulating extended outage)
7. **Assert:** No crash, no unhandled exceptions — publishers are in retry loops
8. **Restart the database**
9. **Assert:** Publishers resume heartbeats, health check returns to `Healthy`
10. **Assert:** Rebalance re-establishes partition ownership
11. **Assert:** Remaining unprocessed messages are eventually published
12. **Assert:** No duplicate messages (messages that were fetched but not sent before DB went down should be retried on recovery)
13. **Assert:** Both publishers have partitions assigned (verify via `outbox_partitions` table)

**What can go wrong in production:**

- Application cannot write new messages during DB outage (application transaction also fails — separate concern)
- After long outage, partition ownership may be in limbo until grace period + rebalance completes (~2 minutes)
- Dead publisher rows accumulate in `outbox_publishers` table

**Runbook actions:**

- Monitor error logs from all loops
- After recovery, verify partition distribution: `SELECT partition_id, owner_publisher_id FROM outbox_partitions`
- Clean up stale publisher rows: `DELETE FROM outbox_publishers WHERE last_heartbeat_utc < NOW() - INTERVAL '1 hour'`

---

## Scenario 3: Process Kill Mid-Batch (SIGKILL / OOM Kill)

**What we're testing:** When a publisher is killed mid-send (no graceful shutdown), surviving publishers claim orphaned partitions and messages are redelivered.

**Setup:**

1. Start 2 publisher instances (A and B)
2. Wait for partition rebalance
3. Insert 200 messages with a mix of partition keys ensuring both publishers have work

**Test steps:**

1. Verify both publishers are actively publishing (check consumer for messages from both)
2. **SIGKILL publisher A** (`kill -9 <pid>`) — no graceful shutdown, no `UnregisterPublisherAsync`
3. **Assert:** Publisher A's row remains in `outbox_publishers` table (not cleaned up)
4. Wait `HeartbeatTimeoutSeconds` (30s) — publisher B detects A's stale heartbeat
5. **Assert:** Publisher B's rebalance marks A's partitions with grace period
6. Wait `PartitionGracePeriodSeconds` (60s) — grace period expires
7. **Assert:** Publisher B claims A's orphaned partitions via rebalance or orphan sweep
8. **Assert:** Messages from A's partitions are now fetched and processed by B
9. **Assert:** ALL messages eventually published to consumer
10. **Assert:** Some messages may be duplicated (sent by A before kill, then re-sent by B) — consumer must handle this
11. **Assert:** After stabilization, outbox table is empty

**Recovery time measurement:**

- Record timestamp of SIGKILL
- Record timestamp of last message delivered to consumer
- Expected: ≤ `HeartbeatTimeout + GracePeriod + RebalanceInterval` = ~120 seconds worst case

**Runbook actions:**

- Verify dead publisher's partitions were redistributed: `SELECT * FROM outbox_partitions WHERE owner_publisher_id = '<dead-id>'`
- Clean up dead publisher: `DELETE FROM outbox_publishers WHERE publisher_id = '<dead-id>'`
- Monitor for duplicate messages downstream

---

## Scenario 4: Graceful Shutdown (SIGTERM / Deployment)

**What we're testing:** On graceful shutdown, partition ownership is released immediately, allowing a new publisher to pick up messages without delay.

**Setup:**

1. Start a publisher instance
2. Insert 100 messages

**Test steps:**

1. Wait for publisher to begin processing (at least one batch fetched)
2. **Send SIGTERM** (graceful shutdown via `StopAsync`)
3. **Assert:** `UnregisterPublisherAsync` is called (releases partition ownership)
4. Immediately start a new publisher instance
5. **Assert:** The new publisher claims partitions and processes remaining messages within seconds
6. **Assert:** All messages eventually published

**Timing verification:**

- After SIGTERM, measure time until remaining messages are processed
- Should be < 10 seconds (rebalance + poll interval)

---

## Scenario 5: Poison Message (Oversized / Malformed Payload)

**What we're testing:** A message that consistently fails transport send with a non-transient error (e.g., too large for EventHub batch) is dead-lettered inline within `MaxPublishAttempts * RetryBackoffMaxMs` of being fetched (a few seconds with defaults).

**Setup:**

1. Start a publisher instance with `MaxPublishAttempts = 3`
2. Insert 1 message with a payload larger than `MaxBatchSizeBytes` (e.g., 2MB for EventHub's 1MB limit)
3. Insert 5 normal messages with the same topic/partition key (to verify they're not blocked)

**Test steps:**

1. Wait for publisher to attempt sending the oversized message
2. **Assert:** `SendAsync` throws `InvalidOperationException: Message too large`
3. **Assert:** `IOutboxTransport.IsTransient` classifies this as non-transient — in-memory attempt counter incremented (circuit breaker NOT fed)
4. **Assert:** After `MaxPublishAttempts` non-transient failures, the publisher calls `DeadLetterAsync` inline — message moves atomically from `outbox` to `outbox_dead_letter`
5. **Assert:** `outbox.messages.dead_lettered` metric incremented
6. **Assert:** The 5 normal messages are published successfully (not blocked by the poison message)
7. **Assert:** Dead letter table contains the oversized message with `last_error` and `attempt_count` populated
8. **Assert:** Outbox table no longer contains the poison message

**Verify no infinite retry:**

- The in-memory attempt counter is a local variable scoped to one group's processing cycle — it cannot grow unbounded across restarts
- On publisher restart, messages are re-fetched with a fresh attempt budget (acceptable under at-least-once semantics)

---

## Scenario 6: Intermittent Transport Failures

**What we're testing:** Under intermittent non-transient failures (2 fail, 1 success pattern), messages still eventually dead-letter instead of retrying forever.

**Setup:**

1. Start a publisher with `MaxPublishAttempts = 5`, `CircuitBreakerFailureThreshold = 10` (high to prevent circuit from opening)
2. Configure transport mock/proxy to fail 2 out of every 3 sends with a non-transient error (so `IsTransient` returns false)
3. Insert 10 messages

**Test steps:**

1. **Assert:** Each failed non-transient send increments the in-memory attempt counter for that (topic, partitionKey) group
2. **Assert:** Each successful send resets the circuit breaker failure count (in-memory attempt counter is scoped to one processing cycle)
3. **Assert:** Messages that succeed are deleted from outbox
4. **Assert:** Messages whose in-memory attempt counter reaches `MaxPublishAttempts` are dead-lettered inline via `DeadLetterAsync`
5. **Assert:** After sufficient iterations, all 10 messages are either published or dead-lettered
6. **Assert:** Outbox table is eventually empty
7. **Assert:** No message retries indefinitely

---

## Scenario 7: Circuit Breaker Does NOT Burn the Attempt Counter

**What we're testing:** When the circuit breaker is open, messages are skipped (left in the outbox) WITHOUT consuming the in-memory attempt counter. Transient failures trip the circuit but do not advance the attempt counter. This prevents messages from being dead-lettered due to a broker outage.

**Setup:**

1. Start a publisher with `MaxPublishAttempts = 3`, `CircuitBreakerFailureThreshold = 2`
2. Insert 10 messages
3. Make the broker unreachable (transient failure — `IsTransient` returns true)

**Test steps:**

1. Wait for 2 consecutive transient failures → circuit opens (attempt counter NOT incremented during these failures)
2. **Assert:** Subsequent fetches for that topic are skipped — messages stay in the outbox untouched
3. **Assert:** No dead-lettering occurs while circuit is open
4. Wait for circuit to half-open, then make broker available
5. **Assert:** Messages sent successfully — they were never charged against their attempt budget during the outage
6. **Assert:** Messages are NOT dead-lettered
7. **Assert:** All 10 messages published

---

## Scenario 8: Multi-Publisher Rebalance Correctness

**What we're testing:** When publishers scale up/down, partitions are redistributed fairly, the grace period prevents dual-processing, and no messages are lost or stuck.

**Setup:**

1. Start publisher A (should own all 64 partitions)
2. Insert 500 messages distributed across many partition keys

**Test steps — Scale Up:**

1. Verify publisher A owns all 64 partitions
2. Start publisher B
3. Wait for rebalance interval (30s)
4. **Assert:** Partitions are split roughly 32/32 between A and B
5. **Assert:** No messages were double-processed during rebalance (verify via consumer — no duplicates)
6. **Assert:** No messages were dropped during handover (all 500 eventually delivered)
7. Start publisher C
8. Wait for rebalance
9. **Assert:** Partitions split roughly 11/11/10 (ceil(32/3) = 11)

**Test steps — Scale Down:**

1. With A, B, C running, gracefully stop C
2. Wait for rebalance
3. **Assert:** C's partitions distributed to A and B (16/16)
4. **Assert:** No message loss during C's shutdown
5. **Assert:** C's partitions are released on graceful shutdown

**Grace period verification:**

1. With A and B running, kill A (SIGKILL — no graceful shutdown)
2. **Assert:** B's rebalance sets `grace_expires_utc` on A's partitions
3. **Assert:** B does NOT process A's partitions until grace expires
4. **Assert:** After grace expires, B claims A's partitions and processes them

---

## Scenario 9: Loop Crash and Auto-Recovery

**What we're testing:** If an internal loop (heartbeat, rebalance, etc.) crashes due to an unexpected exception, the linked CancellationTokenSource cancels all loops, and they restart automatically.

**Setup:**

1. Start a publisher instance
2. Insert messages continuously

**Test steps — Transient crash:**

1. Simulate a transient error that causes the heartbeat loop to throw an unhandled exception (e.g., by temporarily revoking DB permissions for the heartbeat query)
2. **Assert:** All loops are cancelled via linked CTS
3. **Assert:** `ConsecutiveLoopRestarts` incremented to 1
4. **Assert:** Health check reports `Degraded` (loop restarts > 0)
5. **Assert:** Loops restart after backoff delay (2s for first restart)
6. Restore DB permissions
7. **Assert:** After 30 continuous seconds of healthy operation, `ConsecutiveLoopRestarts` resets to 0
8. **Assert:** Health check returns to `Healthy`
9. **Assert:** Messages continue to be published without loss

**Test steps — Persistent crash (escalation to host stop):**

1. Inject a persistent failure that causes loops to crash on every restart
2. **Assert:** Restarts increase: 1, 2, 3, 4, 5
3. **Assert:** Backoff delays double: 2s, 4s, 8s, 16s, 32s
4. **Assert:** After 5 consecutive restarts, `IHostApplicationLifetime.StopApplication()` is called
5. **Assert:** Publisher shuts down cleanly (unregisters publisher)
6. **Assert:** Health check reported `Unhealthy` before shutdown

---

## Scenario 10: Health Check Accuracy

**What we're testing:** The outbox health check correctly reports Healthy, Degraded, and Unhealthy states based on internal publisher state.

**Setup:**

1. Start a publisher instance with `HeartbeatIntervalMs = 5000` (5s)

**Test steps — Healthy state:**

1. Let publisher run normally for 15 seconds
2. **Assert:** Health check returns `Healthy`
3. **Assert:** `data.publishLoopRunning = true`
4. **Assert:** `data.lastHeartbeatUtc` is within last 15s
5. **Assert:** `data.openCircuitBreakers` is absent

**Test steps — Degraded state (open circuits):**

1. Block broker for one topic (e.g., via Toxiproxy on specific port)
2. Wait for circuit breaker to open
3. **Assert:** Health check returns `Degraded`
4. **Assert:** `data.openCircuitBreakers` contains the affected topic name
5. Restore broker connectivity
6. Wait for circuit to close
7. **Assert:** Health check returns `Healthy`

**Test steps — Unhealthy state (stale heartbeat):**

1. Block database connectivity
2. Wait for `HeartbeatIntervalMs * 3` (15s)
3. **Assert:** Health check returns `Unhealthy` with stale heartbeat message
4. Restore DB connectivity
5. **Assert:** Health check returns `Healthy` after next heartbeat succeeds

**Test steps — Unhealthy state (loop stopped):**

1. Force all loops to exit (via host shutdown)
2. **Assert:** Health check returns `Unhealthy` with "publish loop is not running" message

**Test steps — Unhealthy state (never heartbeated):**

1. Start publisher, immediately query health check before first heartbeat completes
2. **Assert:** Health check returns `Unhealthy` (loop started but no heartbeat recorded yet)

**Test steps — Unhealthy state (stale poll):**

1. Start publisher, block DB so poll loop cannot complete
2. Wait for poll staleness threshold
3. **Assert:** Health check returns `Unhealthy` (last poll is stale or never occurred)

---

## Scenario 11: Pending Messages Depth Metric

**What we're testing:** The `outbox.messages.pending` gauge accurately reflects the number of messages waiting in the outbox table.

**Setup:**

1. Start a publisher with broker blocked (so messages accumulate)

**Test steps:**

1. Insert 100 messages
2. Wait for at least one heartbeat cycle (10s)
3. **Assert:** `outbox.messages.pending` gauge = 100 (±10 for timing)
4. Unblock broker
5. Wait for backlog to drain
6. Wait for next heartbeat cycle
7. **Assert:** `outbox.messages.pending` gauge = 0

---

## Scenario 12: Dead Letter Replay

**What we're testing:** Messages in the dead letter table can be replayed (moved back to outbox) and re-processed successfully.

**Setup:**

1. Create a poison message scenario (Scenario 5) to get a message into dead letter
2. Fix the root cause (e.g., increase `MaxBatchSizeBytes` or fix the payload)

**Test steps:**

1. **Assert:** Dead letter table has ≥1 message
2. Call `IDeadLetterManager.ReplayAsync` with the dead-lettered sequence numbers
3. **Assert:** Messages moved from `outbox_dead_letter` back to `outbox` table
4. **Assert:** Replayed messages enter the outbox with no persisted retry state (the in-memory attempt counter starts fresh on the next poll)
5. **Assert:** Messages are eventually published to the broker
6. **Assert:** Consumer receives the replayed messages
7. **Assert:** Dead letter table no longer contains the replayed messages

---

## Scenario 13: Ordering Guarantees Under Failure

**What we're testing:** Per-partition-key ordering is preserved even through failures, circuit breaker cycles, and publisher crashes.

**Setup:**

1. Insert 100 messages for partition key `order-123`, in the desired delivery order (insert order = delivery order)
2. Configure broker to fail intermittently (every 3rd send)

**Test steps:**

1. Start publisher, let it process through failures and retries
2. Collect all messages received by consumer for partition key `order-123`
3. **Assert:** Messages are received in `sequence_number` order, even if some are duplicated
4. **Assert:** Deduplicating by sequence number produces exactly messages 1-100 in order
5. **Assert:** No gaps (every message from 1-100 is present)

**Repeat with publisher crash:**

1. Insert 100 ordered messages
2. Kill publisher at ~50% progress
3. Start new publisher
4. Collect all consumer messages
5. **Assert:** Same ordering guarantees as above (duplicates OK, gaps NOT OK, order violations NOT OK)

---

## Scenario 14: Network Partition (DB Reachable, Broker Not)

**What we're testing:** When the app can reach the DB but not the broker, the circuit breaker activates, messages accumulate safely, and auto-recovery works when network heals.

**Setup:**

1. Start publisher with both DB and broker reachable
2. Insert 50 messages, wait for them to publish

**Test steps:**

1. **Block only broker connectivity** (keep DB reachable)
2. Insert 100 more messages
3. **Assert:** Publish attempts fail, circuit opens after threshold
4. **Assert:** Heartbeat continues successfully (DB is fine)
5. **Assert:** Health check returns `Degraded` (not `Unhealthy` — loop is running, heartbeat is fine)
6. **Assert:** Messages accumulate in outbox table without being dead-lettered (circuit opens on transient failures before the in-memory attempt counter is exhausted)
7. **Assert:** Circuit breaker open-close cycles are visible in metrics
9. **Restore broker connectivity**
10. **Assert:** Circuit half-opens, probe succeeds, circuit closes
11. **Assert:** All 100 accumulated messages published
12. **Assert:** Health check returns `Healthy`

---

## Unit Test Coverage for Event Handler Isolation

The following scenarios are covered by unit tests in `OutboxPublisherServiceTests` (no infrastructure required):

1. **`OnMessagePublishedAsync` throws after successful send:** Retry count must NOT be incremented. Delete must still proceed. Circuit breaker must NOT record a failure.
2. **`OnCircuitBreakerStateChangedAsync` throws after circuit recovery:** Same invariant — transport succeeded, handler failure is irrelevant.
3. **`OnMessageDeadLetteredAsync` throws with healthy messages in batch:** Healthy messages must still be sent to the transport (not blocked by poison handler failure).
4. **`OnMessageDeadLetteredAsync` throws:** Healthy messages must still be processed normally.

---

## Test Execution Notes

### Timing Considerations

- Tests involving heartbeat/rebalance timeouts are inherently slow (30-90 second waits)
- Use shorter timeout values in test configuration (e.g., `HeartbeatTimeoutSeconds = 5`, `PartitionGracePeriodSeconds = 10`)
- Still expect tests to take 30-120 seconds each

### Consumer Verification

- All tests should verify message delivery via an actual consumer reading from the broker topic
- Consumer should collect messages in a thread-safe list with timestamps
- Ordering assertions should account for at-least-once semantics (duplicates allowed, order violations not)

### Cleanup Between Tests

- Truncate `outbox`, `outbox_dead_letter`, `outbox_publishers`, `outbox_partitions` tables
- Re-seed partitions (32 rows)
- Purge broker topics or use unique topic names per test
- Kill all publisher instances

### Metrics Verification

- Use `System.Diagnostics.Metrics.MeterListener` to capture metric values in tests
- Or configure an in-memory metrics exporter

### Infrastructure Simulation Tools

- **Toxiproxy:** Preferred for network fault injection (add latency, drop connections, limit bandwidth)
- **Docker stop/start:** For simulating full service outages
- **iptables/nftables:** For fine-grained network partition simulation
- **Testcontainers:** For spinning up PostgreSQL/Kafka/Redpanda per test suite
