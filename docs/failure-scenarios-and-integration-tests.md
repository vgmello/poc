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
8. **Assert:** Retry counts on messages are being incremented (transport failure path)
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
12. **Assert:** No duplicate messages (messages that were leased but not sent before DB went down should be retried exactly once after recovery)
13. **Assert:** Both publishers have partitions assigned (verify via `outbox_partitions` table)

**What can go wrong in production:**

- Application cannot write new messages during DB outage (application transaction also fails — separate concern)
- After long outage, partition ownership may be in limbo until grace period + rebalance completes (~2 minutes)
- Dead producer rows accumulate in `outbox_producers` table

**Runbook actions:**

- Monitor error logs from all loops
- After recovery, verify partition distribution: `SELECT partition_id, owner_producer_id FROM outbox_partitions`
- Clean up stale producer rows: `DELETE FROM outbox_producers WHERE last_heartbeat_utc < NOW() - INTERVAL '1 hour'`

---

## Scenario 3: Process Kill Mid-Batch (SIGKILL / OOM Kill)

**What we're testing:** When a publisher is killed mid-send (no graceful shutdown), in-flight leases expire, surviving publishers claim orphaned partitions, and messages are redelivered.

**Setup:**

1. Start 2 publisher instances (A and B)
2. Wait for partition rebalance
3. Insert 200 messages with a mix of partition keys ensuring both publishers have work

**Test steps:**

1. Verify both publishers are actively publishing (check consumer for messages from both)
2. **SIGKILL publisher A** (`kill -9 <pid>`) — no graceful shutdown, no `UnregisterProducerAsync`
3. **Assert:** Publisher A's row remains in `outbox_producers` table (not cleaned up)
4. Wait `HeartbeatTimeoutSeconds` (30s) — publisher B detects A's stale heartbeat
5. **Assert:** Publisher B's rebalance marks A's partitions with grace period
6. Wait `PartitionGracePeriodSeconds` (60s) — grace period expires
7. **Assert:** Publisher B claims A's orphaned partitions via rebalance or orphan sweep
8. Wait `LeaseDurationSeconds` (45s) — A's in-flight leases expire
9. **Assert:** Messages that were leased by A are now re-leased by B
10. **Assert:** Retry count incremented for those messages (lease expired, not explicitly released)
11. **Assert:** ALL messages eventually published to consumer
12. **Assert:** Some messages may be duplicated (sent by A before kill, then re-sent by B) — consumer must handle this
13. **Assert:** After stabilization, outbox table is empty

**Recovery time measurement:**

- Record timestamp of SIGKILL
- Record timestamp of last message delivered to consumer
- Expected: ≤ `HeartbeatTimeout + GracePeriod + LeaseExpiry + RebalanceInterval` = ~165 seconds worst case

**Runbook actions:**

- Verify dead producer's partitions were redistributed: `SELECT * FROM outbox_partitions WHERE owner_producer_id = '<dead-id>'`
- Clean up dead producer: `DELETE FROM outbox_producers WHERE producer_id = '<dead-id>'`
- Monitor for duplicate messages downstream

---

## Scenario 4: Graceful Shutdown (SIGTERM / Deployment)

**What we're testing:** On graceful shutdown, in-flight leases are released immediately (not waiting for lease expiry), reducing message processing delay.

**Setup:**

1. Start a publisher instance
2. Configure `LeaseDurationSeconds = 120` (long lease to make the test clear)
3. Insert 100 messages

**Test steps:**

1. Wait for publisher to begin processing (at least one batch leased)
2. **Send SIGTERM** (graceful shutdown via `StopAsync`)
3. **Assert:** The `PublishLoopAsync` `finally` block releases in-flight leased messages via `ReleaseLeaseAsync(..., incrementRetry: false, CancellationToken.None)` (leased_until_utc = NULL)
4. **Assert:** `UnregisterProducerAsync` is called (releases partition ownership, not message leases — that happened in step 3)
5. Immediately start a new publisher instance
6. **Assert:** The new publisher can lease and process the previously in-flight messages within seconds (not waiting 120s for lease expiry)
7. **Assert:** All messages eventually published

**Timing verification:**

- After SIGTERM, measure time until previously-leased messages are re-processed
- Should be < 10 seconds (rebalance + poll interval), NOT 120 seconds (lease expiry)

---

## Scenario 5: Poison Message (Oversized / Malformed Payload)

**What we're testing:** A message that consistently fails transport send (e.g., too large for EventHub batch) eventually gets dead-lettered instead of retrying infinitely.

**Setup:**

1. Start a publisher instance with `MaxRetryCount = 3`
2. Insert 1 message with a payload larger than `MaxBatchSizeBytes` (e.g., 2MB for EventHub's 1MB limit)
3. Insert 5 normal messages with the same topic/partition key (to verify they're not blocked)

**Test steps:**

1. Wait for publisher to attempt sending the oversized message
2. **Assert:** `SendAsync` throws `InvalidOperationException: Message too large`
3. **Assert:** `ReleaseLeaseAsync` called with `incrementRetry: true`
4. **Assert:** After 3 failures, message's `retry_count` = 3 (incremented on each explicit release)
5. **Assert:** `LeaseBatchAsync` no longer returns this message (SQL filter: `retry_count < @max_retry_count`)
6. **Assert:** `DeadLetterSweepLoopAsync` picks up the message and calls `SweepDeadLettersAsync`, which moves it to `outbox_dead_letter` table
7. **Assert:** `outbox.messages.dead_lettered` metric incremented
8. **Assert:** The 5 normal messages are published successfully (not blocked by the poison message)
9. **Assert:** Dead letter table contains the oversized message with `last_error` populated

**Verify no infinite retry:**

- Track `retry_count` after each lease-release cycle
- Must increment by 1 each time (because `incrementRetry: true`)

---

## Scenario 6: Intermittent Transport Failures

**What we're testing:** Under intermittent failures (2 fail, 1 success pattern), messages still eventually dead-letter instead of retrying forever.

**Setup:**

1. Start a publisher with `MaxRetryCount = 5`, `CircuitBreakerFailureThreshold = 10` (high to prevent circuit from opening)
2. Configure transport mock/proxy to fail 2 out of every 3 sends
3. Insert 10 messages

**Test steps:**

1. **Assert:** Each failed send increments `retry_count` via `ReleaseLeaseAsync(incrementRetry: true)`
2. **Assert:** Each successful send resets the circuit breaker failure count (but does NOT reset `retry_count` — that only resets on replay from dead letter)
3. **Assert:** Messages that succeed are deleted from outbox
4. **Assert:** Messages that accumulate `retry_count >= 5` are dead-lettered
5. **Assert:** After sufficient iterations, all 10 messages are either published or dead-lettered
6. **Assert:** Outbox table is eventually empty
7. **Assert:** No message retries indefinitely

---

## Scenario 7: Circuit Breaker Does NOT Burn Retry Counts

**What we're testing:** When the circuit breaker is open, messages are released WITHOUT incrementing retry count. This prevents messages from being dead-lettered due to broker outage (not message-level failure).

**Setup:**

1. Start a publisher with `MaxRetryCount = 3`, `CircuitBreakerFailureThreshold = 2`
2. Insert 10 messages
3. Make the broker unreachable

**Test steps:**

1. Wait for 2 consecutive failures → circuit opens
2. **Assert:** Subsequent leases are immediately released with `incrementRetry: false`
3. **Assert:** `retry_count` stays at value from before circuit opened (incremented only during actual send attempts, not during circuit-open releases)
4. Wait for circuit to half-open, then make broker available
5. **Assert:** Messages sent successfully with `retry_count` ≤ 2 (from the initial failures before circuit opened)
6. **Assert:** Messages are NOT dead-lettered (retry count did not reach `MaxRetryCount`)
7. **Assert:** All 10 messages published

---

## Scenario 8: Multi-Publisher Rebalance Correctness

**What we're testing:** When publishers scale up/down, partitions are redistributed fairly, the grace period prevents dual-processing, and no messages are lost or stuck.

**Setup:**

1. Start publisher A (should own all 32 partitions)
2. Insert 500 messages distributed across many partition keys

**Test steps — Scale Up:**

1. Verify publisher A owns all 32 partitions
2. Start publisher B
3. Wait for rebalance interval (30s)
4. **Assert:** Partitions are split roughly 16/16 between A and B
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
5. **Assert:** Messages that C had leased are released on graceful shutdown

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
5. **Assert:** Publisher shuts down cleanly (unregisters producer)
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
4. **Assert:** `retry_count` reset to 0 in the outbox table
5. **Assert:** Messages are eventually published to the broker
6. **Assert:** Consumer receives the replayed messages
7. **Assert:** Dead letter table no longer contains the replayed messages

---

## Scenario 13: Ordering Guarantees Under Failure

**What we're testing:** Per-partition-key ordering is preserved even through failures, circuit breaker cycles, and publisher crashes.

**Setup:**

1. Insert 100 messages for partition key `order-123`, numbered 1-100 in `EventOrdinal`
2. Configure broker to fail intermittently (every 3rd send)

**Test steps:**

1. Start publisher, let it process through failures and retries
2. Collect all messages received by consumer for partition key `order-123`
3. **Assert:** Messages are received in order (by `EventOrdinal`), even if some are duplicated
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
6. **Assert:** Messages accumulate in outbox table
7. **Assert:** `retry_count` increments on each failed send
8. **Assert:** Circuit breaker open-close cycles are visible in metrics
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
4. **`OnMessageDeadLetteredAsync` throws:** Healthy messages must be processed or released, not stuck until lease expiry.

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

- Truncate `outbox`, `outbox_dead_letter`, `outbox_producers`, `outbox_partitions` tables
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
