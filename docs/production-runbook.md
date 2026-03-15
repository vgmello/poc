# Outbox Library — Production Runbook & Failure Scenario Guide

This document covers all known failure scenarios, their symptoms, auto-recovery behavior, operator actions, and known bugs/limitations discovered during code review. Use this as the primary reference for incident response.

---

## Table of Contents

1. [Architecture Quick Reference](#architecture-quick-reference)
2. [Health Check Reference](#health-check-reference)
3. [Key Metrics](#key-metrics)
4. [Failure Scenarios](#failure-scenarios)
   - [FS-1: Broker Down (EventHub/Kafka Unreachable)](#fs-1-broker-down-eventhubkafka-unreachable)
   - [FS-2: Database Down (PostgreSQL/SQL Server Unreachable)](#fs-2-database-down-postgresqlsql-server-unreachable)
   - [FS-3: Process Kill (SIGKILL / OOM Kill)](#fs-3-process-kill-sigkill--oom-kill)
   - [FS-4: Graceful Shutdown (SIGTERM / Rolling Deployment)](#fs-4-graceful-shutdown-sigterm--rolling-deployment)
   - [FS-5: Poison Message (Oversized/Malformed Payload)](#fs-5-poison-message-oversizedmalformed-payload)
   - [FS-6: Network Partition (DB Reachable, Broker Not)](#fs-6-network-partition-db-reachable-broker-not)
   - [FS-7: Network Partition (Broker Reachable, DB Not)](#fs-7-network-partition-broker-reachable-db-not)
   - [FS-8: Intermittent Transport Failures](#fs-8-intermittent-transport-failures)
   - [FS-9: Circuit Breaker Stuck Open](#fs-9-circuit-breaker-stuck-open)
   - [FS-10: Partition Ownership Stuck / No Publisher Processing Messages](#fs-10-partition-ownership-stuck--no-publisher-processing-messages)
   - [FS-11: Multi-Publisher Rebalance During Deployments](#fs-11-multi-publisher-rebalance-during-deployments)
   - [FS-12: Outbox Table Growing Unbounded](#fs-12-outbox-table-growing-unbounded)
   - [FS-13: Dead Letter Queue Growing](#fs-13-dead-letter-queue-growing)
   - [FS-14: Duplicate Messages Downstream](#fs-14-duplicate-messages-downstream)
   - [FS-15: Out-of-Order Messages Downstream](#fs-15-out-of-order-messages-downstream)
   - [FS-16: EventHub Batch Splitting Partial Failure](#fs-16-eventhub-batch-splitting-partial-failure)
   - [FS-17: Kafka Flush Timeout / Ghost Writes](#fs-17-kafka-flush-timeout--ghost-writes)
   - [FS-18: Corrupted Headers in Outbox Rows](#fs-18-corrupted-headers-in-outbox-rows)
   - [FS-19: Health Check Reports Healthy But Publisher Is Stuck](#fs-19-health-check-reports-healthy-but-publisher-is-stuck)
   - [FS-20: Loop Crash Escalation and Host Shutdown](#fs-20-loop-crash-escalation-and-host-shutdown)
   - [FS-21: Database Failover / Connection Pool Exhaustion](#fs-21-database-failover--connection-pool-exhaustion)
   - [FS-22: Partition Hash Collision (INT_MIN Edge Case)](#fs-22-partition-hash-collision-intmin-edge-case)
5. [Known Bugs & Limitations](#known-bugs--limitations)
6. [Emergency Procedures](#emergency-procedures)
7. [Monitoring & Alerting Recommendations](#monitoring--alerting-recommendations)

---

## Architecture Quick Reference

```
Application Transaction
├── Write business data
└── Write event to outbox table (same transaction)
         ↓
OutboxPublisherService (5 concurrent loops):
├── PublishLoop    — Lease → Send → Delete (core path)
├── HeartbeatLoop  — Keep producer alive (10s)
├── RebalanceLoop  — Redistribute partitions (30s)
├── OrphanSweep    — Claim unowned partitions (60s)
└── DeadLetterSweep — Quarantine poison messages (60s)
         ↓
Broker (EventHub / Kafka)
         ↓
Consumer
```

**Key defaults:**
| Parameter | Default | Purpose |
|-----------|---------|---------|
| BatchSize | 100 | Messages per lease cycle |
| LeaseDurationSeconds | 45 | How long a lease is held |
| MaxRetryCount | 5 | Retries before dead-lettering |
| HeartbeatIntervalMs | 10,000 | Heartbeat frequency |
| HeartbeatTimeoutSeconds | 30 | Stale producer detection |
| PartitionGracePeriodSeconds | 60 | Safety window on partition handover |
| CircuitBreakerFailureThreshold | 3 | Consecutive failures to open circuit |
| CircuitBreakerOpenDurationSeconds | 30 | Time before half-open probe |

---

## Health Check Reference

The health check at `/health` reports three states:

| State | Condition | What It Means |
|-------|-----------|---------------|
| **Unhealthy** | Publish loop not running | Service is down or crashed beyond restart limit |
| **Unhealthy** | Heartbeat stale > 3x interval (30s) | DB likely unreachable |
| **Unhealthy** | No polls > 3x max interval (15s) | Publish loop stuck |
| **Degraded** | Any circuit breaker open | Broker partially unreachable |
| **Degraded** | Loop restarts > 0 | Transient internal failures occurred |
| **Healthy** | All checks pass | Normal operation |

**KNOWN LIMITATION:** If the publisher has never successfully heartbeated (DB unreachable from startup), the health check reports **Healthy** instead of Unhealthy because the staleness check requires at least one successful heartbeat. See [FS-19](#fs-19-health-check-reports-healthy-but-publisher-is-stuck).

---

## Key Metrics

| Metric | Type | Alert On |
|--------|------|----------|
| `outbox.messages.published` | Counter | Flat for > 5 min during expected traffic |
| `outbox.messages.dead_lettered` | Counter | Any increment (investigate cause) |
| `outbox.messages.pending` | Gauge | > 1000 sustained, or growing monotonically |
| `outbox.circuit_breaker.state_changes` | Counter | Any increment (broker issues) |
| `outbox.publish.failures` | Counter | > 10/min sustained |
| `outbox.publish.duration` | Histogram | p99 > 10s (transport slowness) |
| `outbox.poll.duration` | Histogram | p99 > 5s (DB slowness) |
| `outbox.poll.batch_size` | Histogram | Consistently 0 when pending > 0 (partition issue) |

---

## Failure Scenarios

### FS-1: Broker Down (EventHub/Kafka Unreachable)

**Symptoms:**
- `outbox.publish.failures` counter increasing
- `outbox.circuit_breaker.state_changes` incremented
- Health check returns `Degraded` with open circuit topics listed
- `outbox.messages.pending` gauge increasing
- Error logs: transport send failures

**What happens automatically:**
1. Publisher attempts to send, fails (3 consecutive failures by default)
2. Circuit breaker opens for the affected topic
3. Leased messages are released **without** incrementing `retry_count` (preserves retry budget)
4. Publisher continues heartbeating and managing partitions normally
5. After 30s (`CircuitBreakerOpenDurationSeconds`), circuit transitions to HalfOpen
6. One probe batch is sent — if it fails, circuit re-opens; if it succeeds, circuit closes
7. On circuit close, backlog drains automatically

**Auto-recovery:** YES — fully automatic when broker becomes reachable.

**Recovery time:** Backlog drains at ~`BatchSize / PollInterval` messages/second after circuit closes.

**Operator actions:**
- **DO NOT restart the publisher** — it is correctly backing off
- Monitor `outbox.messages.pending` for backlog depth
- If backlog is large after recovery, temporarily increase `BatchSize`
- If broker is expected to be down for hours, consider scaling down publisher instances to reduce log noise
- Alert threshold: circuit open > 5 minutes continuously

**Risk during this scenario:**
- Outbox table grows with incoming messages — monitor disk space
- Burst on recovery may overwhelm downstream consumers — consider consumer auto-scaling
- Messages from the initial 3 failures (before circuit opened) WILL have their `retry_count` incremented

---

### FS-2: Database Down (PostgreSQL/SQL Server Unreachable)

**Symptoms:**
- All store operations fail with transient error logs
- Health check transitions to `Unhealthy` (stale heartbeat, stale polls)
- No messages being published (cannot lease)
- Application transactions also fail (separate concern)

**What happens automatically:**
1. All 5 loops catch exceptions and continue retrying
2. HeartbeatLoop retries every 10s, PublishLoop backs off to `MaxPollIntervalMs` (5s)
3. No messages are lost — they remain safely in the outbox table
4. When DB recovers, all loops resume normal operation
5. Producer re-establishes heartbeat, rebalance redistributes partitions if needed

**Auto-recovery:** YES — fully automatic when DB becomes reachable.

**KNOWN LIMITATION:** The loops catch-and-continue all exceptions. The restart machinery (`RunLoopsWithRestartAsync`, `StopApplication` after 5 restarts) is **never triggered** during a DB outage because no exception escapes the loop's catch block. The pod stays up indefinitely but non-functional. See [Known Bug #5](#known-bugs--limitations).

**Recovery time:** ~30-60 seconds after DB is available (heartbeat + rebalance cycle).

**Operator actions:**
- Monitor error log volume — each loop logs every 5-10s during outage
- After recovery, verify partition distribution:
  ```sql
  SELECT partition_id, owner_producer_id FROM outbox_partitions ORDER BY partition_id;
  ```
- Clean up stale producer rows:
  ```sql
  -- PostgreSQL
  DELETE FROM outbox_producers WHERE last_heartbeat_utc < clock_timestamp() - INTERVAL '1 hour';
  -- SQL Server
  DELETE FROM dbo.OutboxProducers WHERE LastHeartbeatUtc < DATEADD(HOUR, -1, SYSUTCDATETIME());
  ```
- After long outage (> `PartitionGracePeriodSeconds`), expect full rebalance

---

### FS-3: Process Kill (SIGKILL / OOM Kill)

**Symptoms:**
- Publisher process disappears suddenly
- No `UnregisterProducerAsync` called — producer row remains in DB
- Leased messages remain locked until `LeaseDurationSeconds` expires
- Surviving publishers detect stale heartbeat after `HeartbeatTimeoutSeconds` (30s)

**What happens automatically:**
1. Dead producer's heartbeat becomes stale
2. Surviving publishers detect staleness during rebalance loop (every 30s)
3. Dead producer's partitions enter grace period (`PartitionGracePeriodSeconds` = 60s)
4. After grace period, surviving publishers claim orphaned partitions
5. Dead producer's leases expire after `LeaseDurationSeconds` (45s)
6. Expired-lease messages are re-leased by surviving publishers with `retry_count` incremented
7. All messages eventually published (some may be duplicated)

**Auto-recovery:** YES — fully automatic via lease expiry + partition rebalance.

**Recovery time (worst case):**
`HeartbeatTimeout (30s) + GracePeriod (60s) + LeaseExpiry (45s) + RebalanceInterval (30s)` ≈ **165 seconds**

**Operator actions:**
- Verify dead producer's partitions were redistributed:
  ```sql
  SELECT * FROM outbox_partitions WHERE owner_producer_id = '<dead-producer-id>';
  ```
- Clean up dead producer row:
  ```sql
  DELETE FROM outbox_producers WHERE producer_id = '<dead-producer-id>';
  ```
- Monitor for duplicate messages downstream
- If multiple publishers die simultaneously, manually verify all 32 partitions are owned:
  ```sql
  SELECT COUNT(*) FROM outbox_partitions WHERE owner_producer_id IS NULL;
  ```

---

### FS-4: Graceful Shutdown (SIGTERM / Rolling Deployment)

**Symptoms:**
- Publisher stops processing (expected during deployment)
- `UnregisterProducerAsync` called — producer row deleted, partitions released

**What happens automatically:**
1. `CancellationToken` is triggered
2. All loops receive cancellation and exit
3. In-flight leases are released (leased_until_utc = NULL) — NOT waiting for lease expiry
4. Producer unregistered from DB
5. Partitions immediately available for other publishers
6. New publisher instance (from deployment) claims partitions on startup

**Auto-recovery:** YES — designed for zero-downtime rolling deployments.

**Recovery time:** New publisher processes released messages within seconds (poll interval).

**Operator actions:**
- During rolling deployments, stagger instance restarts by at least `RebalanceIntervalMs` (30s)
- Monitor that new instances claim partitions promptly
- If using Kubernetes, set `terminationGracePeriodSeconds >= 60` to allow in-flight operations to complete

---

### FS-5: Poison Message (Oversized/Malformed Payload)

**Symptoms:**
- `outbox.messages.dead_lettered` counter incrementing
- `outbox.publish.failures` counter incrementing for specific messages
- Same sequence numbers appearing in error logs repeatedly
- Other messages in the same partition key continue processing normally

**What happens automatically:**
1. Transport `SendAsync` throws for the message (e.g., too large for EventHub batch)
2. `ReleaseLeaseAsync(incrementRetry: true)` increments `retry_count`
3. After `MaxRetryCount` (5) failures, message is moved to `outbox_dead_letter` table
4. `last_error` column populated with exception message
5. Remaining healthy messages in the batch continue processing

**Auto-recovery:** Message is automatically quarantined. No manual intervention needed unless the message needs to be delivered.

**Operator actions:**
- Query dead letter queue:
  ```sql
  SELECT sequence_number, topic_name, partition_key, event_type, last_error, dead_lettered_at_utc
  FROM outbox_dead_letter ORDER BY dead_lettered_at_utc DESC;
  ```
- If the root cause is fixable (e.g., increase batch size limit), fix it then replay:
  ```csharp
  await deadLetterManager.ReplayAsync(new[] { sequenceNumber }, ct);
  ```
- If the message is unrecoverable, purge it:
  ```csharp
  await deadLetterManager.PurgeAsync(new[] { sequenceNumber }, ct);
  ```

---

### FS-6: Network Partition (DB Reachable, Broker Not)

**Symptoms:**
- `outbox.publish.failures` increasing
- Circuit breaker opens
- Health check: `Degraded` (not Unhealthy — heartbeat still succeeds)
- Messages accumulate in outbox table
- Heartbeat loop operates normally

**What happens automatically:**
Same as [FS-1](#fs-1-broker-down-eventhubkafka-unreachable). The key distinction: because the DB is reachable, heartbeats continue, partition ownership is maintained, and the health check correctly reports `Degraded` (not `Unhealthy`).

**Auto-recovery:** YES — same as FS-1.

**Operator actions:**
- Same as FS-1
- Additionally verify network connectivity between publisher and broker
- This is the safest failure mode — data is preserved in DB, no partition ownership disruption

---

### FS-7: Network Partition (Broker Reachable, DB Not)

**Symptoms:**
- Same as [FS-2](#fs-2-database-down-postgresqlsql-server-unreachable)
- Heartbeat fails, lease operations fail, rebalance fails
- Health check: `Unhealthy`
- Even though broker is reachable, no messages can be leased

**What happens automatically:**
Same as FS-2. The publisher cannot function without the DB even if the broker is healthy.

**Auto-recovery:** YES — same as FS-2.

**Operator actions:**
- Same as FS-2
- Investigate DB-specific network path

---

### FS-8: Intermittent Transport Failures

**Symptoms:**
- `outbox.publish.failures` incrementing sporadically
- `retry_count` on some messages increasing
- Some messages dead-lettered despite being individually valid
- Circuit breaker may or may not open (depends on failure pattern)

**What happens automatically:**
1. Each failed send increments `retry_count` via `ReleaseLeaseAsync(incrementRetry: true)`
2. Each successful send deletes the message from outbox
3. Messages that accumulate `retry_count >= MaxRetryCount` are dead-lettered
4. Circuit breaker tracks consecutive failures per topic

**Risk:** Under a pattern of 2 fails → 1 success → 2 fails, `retry_count` grows steadily. A valid message can be dead-lettered if it happens to be leased during failure windows enough times. The `retry_count` is **per-message**, not per-send-attempt, and is not reset on successful sends of other messages.

**Operator actions:**
- If seeing valid messages dead-lettered due to intermittent failures, increase `MaxRetryCount`
- Investigate root cause of intermittent failures (network, broker load, message size variance)
- Replay dead-lettered messages after root cause is resolved

---

### FS-9: Circuit Breaker Stuck Open

**Symptoms:**
- Health check: `Degraded` with circuit open for one or more topics
- `outbox.messages.pending` growing
- No publish attempts visible in logs (circuit prevents sending)

**What happens automatically:**
- Every `CircuitBreakerOpenDurationSeconds` (30s), circuit transitions to HalfOpen
- One probe batch is attempted
- If probe fails, circuit re-opens for another 30s
- If probe succeeds, circuit closes and backlog drains

**Possible causes of stuck-open:**
- Broker is partially available (accepts connections but rejects sends)
- DNS resolution intermittent
- Authentication/authorization expired (token/certificate expiry)
- Topic deleted or misconfigured on broker side
- EventHub namespace throttling (429 responses)

**Operator actions:**
- Check broker health independently (outside the publisher)
- Check authentication credentials / managed identity
- Check topic existence on broker
- If broker is healthy but circuit stays open, restart the publisher to clear circuit state (circuit breaker is in-memory only)
- Review error messages in logs during HalfOpen probe attempts

---

### FS-10: Partition Ownership Stuck / No Publisher Processing Messages

**Symptoms:**
- `outbox.messages.pending` > 0 and not decreasing
- `outbox.poll.batch_size` histogram shows 0
- Publisher is running and healthy
- Messages exist in outbox table

**Possible causes:**
1. Publisher owns no partitions (rebalance not completed)
2. All messages hash to partitions owned by a dead publisher
3. `ABS(INT_MIN)` hash bug — messages hash to negative partition ID (see [FS-22](#fs-22-partition-hash-collision-intmin-edge-case))
4. Grace period not expired on newly assigned partitions
5. All messages have `retry_count >= MaxRetryCount` but haven't been swept yet

**Operator actions:**
- Check partition ownership:
  ```sql
  SELECT partition_id, owner_producer_id, grace_expires_utc FROM outbox_partitions ORDER BY partition_id;
  ```
- Check for unowned partitions:
  ```sql
  SELECT COUNT(*) FROM outbox_partitions WHERE owner_producer_id IS NULL;
  ```
- Check for messages stuck on specific partitions:
  ```sql
  -- PostgreSQL
  SELECT ABS(hashtext(partition_key)) % 32 AS bucket, COUNT(*) FROM outbox GROUP BY bucket;
  -- SQL Server
  SELECT ABS(CHECKSUM(PartitionKey)) % 32 AS Bucket, COUNT(*) FROM dbo.Outbox GROUP BY ABS(CHECKSUM(PartitionKey)) % 32;
  ```
- Check for negative hash buckets (see FS-22):
  ```sql
  -- PostgreSQL
  SELECT * FROM outbox WHERE hashtext(partition_key) = -2147483648;
  -- SQL Server
  SELECT * FROM dbo.Outbox WHERE CHECKSUM(PartitionKey) = -2147483648;
  ```
- Force rebalance by restarting the publisher

---

### FS-11: Multi-Publisher Rebalance During Deployments

**Symptoms:**
- During rolling deployment, brief period of uneven partition distribution
- Possible duplicate messages during partition handover
- Brief processing delays while grace periods expire

**What happens automatically:**
1. Old instance gracefully shuts down, releases partitions
2. New instance starts, registers producer, triggers rebalance
3. Rebalance distributes partitions fairly across active producers
4. Grace period prevents new owner from processing during handover window

**Risk:** If deployment replaces all instances simultaneously, all partitions become unowned for up to `RebalanceIntervalMs + PartitionGracePeriodSeconds` (90s).

**Operator actions:**
- Always use rolling deployments (one instance at a time)
- Wait at least `RebalanceIntervalMs` (30s) between instance restarts
- After deployment, verify partition distribution:
  ```sql
  SELECT owner_producer_id, COUNT(*) FROM outbox_partitions GROUP BY owner_producer_id;
  ```
- Expected: roughly equal partition counts per publisher (ceil(32 / N))

---

### FS-12: Outbox Table Growing Unbounded

**Symptoms:**
- `outbox.messages.pending` gauge continuously increasing
- Outbox table row count growing
- Disk space pressure on DB

**Possible causes:**
1. Broker down → FS-1
2. All circuits open → FS-9
3. Publisher not running
4. Partition ownership stuck → FS-10
5. Incoming message rate exceeds processing capacity
6. `MaxRetryCount` too high + intermittent failures = slow drain

**Operator actions:**
- Identify root cause from the list above
- Monitor table size:
  ```sql
  -- PostgreSQL
  SELECT pg_size_pretty(pg_relation_size('outbox'));
  -- SQL Server
  EXEC sp_spaceused 'dbo.Outbox';
  ```
- If capacity issue, scale up publisher instances (partitions are distributed automatically)
- If stuck, see FS-10 diagnostic queries
- **NEVER** manually DELETE from the outbox table — this can cause message loss

---

### FS-13: Dead Letter Queue Growing

**Symptoms:**
- `outbox.messages.dead_lettered` counter increasing
- `outbox_dead_letter` table row count growing

**Possible causes:**
1. Poison messages (payload too large, malformed headers)
2. Intermittent failures exhausting `MaxRetryCount` for valid messages
3. `MaxRetryCount` too low for the failure pattern
4. Broker rejecting messages (authorization, topic config)

**Operator actions:**
- Investigate dead-lettered messages:
  ```sql
  SELECT event_type, last_error, COUNT(*) FROM outbox_dead_letter GROUP BY event_type, last_error;
  ```
- Fix root cause, then replay:
  ```sql
  -- Find message IDs to replay
  SELECT sequence_number FROM outbox_dead_letter WHERE last_error LIKE '%specific error%';
  ```
  ```csharp
  await deadLetterManager.ReplayAsync(sequenceNumbers, ct);
  ```
- Purge unrecoverable messages:
  ```csharp
  await deadLetterManager.PurgeAllAsync(ct);  // Nuclear option
  ```

---

### FS-14: Duplicate Messages Downstream

**Symptoms:**
- Consumer receives the same message multiple times
- Business logic processes same event twice

**When duplicates occur (by design — at-least-once):**
1. Publisher sends message, broker acknowledges, publisher crashes before `DeletePublishedAsync` — re-sent on recovery
2. Publisher crash mid-batch — surviving publisher re-sends entire batch
3. Circuit breaker half-open probe succeeds but circuit was open for multiple cycles — first batch messages may have been partially sent
4. Kafka `Flush` timeout — messages may be delivered by librdkafka after timeout, then re-sent on retry
5. EventHub batch split — first sub-batch sent, second fails, entire group retried

**Operator actions:**
- **Consumers MUST implement idempotency** — this is the design contract
- Recommended: deduplicate by `SequenceNumber` (unique per message, included as a header/property)
- Recommended: use an idempotency table in the consumer's database
- If duplicates are excessive, investigate whether a specific scenario is causing them

---

### FS-15: Out-of-Order Messages Downstream

**Symptoms:**
- Consumer receives messages for the same partition key in unexpected order

**When ordering can be violated:**
1. **Batch splitting in EventHub:** First sub-batch sent, second fails. On retry, first sub-batch messages are re-sent before second sub-batch → consumers see first-batch duplicates interleaved
2. **In-group ordering not enforced (KNOWN BUG):** Messages within a `(TopicName, PartitionKey)` group are not explicitly sorted by `SequenceNumber`/`EventOrdinal` in the publish code — ordering depends on the DB query's return order
3. **Cross-batch ordering:** Preserved by the unified poll query (`ORDER BY event_datetime_utc, event_ordinal`), assuming single-publisher-per-partition
4. **Partition handover:** During rebalance, if the old and new publisher both process messages for the same partition (grace period violation), order is not guaranteed

**Operator actions:**
- Ordering is guaranteed **per partition key, per batch, assuming single publisher per partition**
- Cross-batch ordering depends on DB query ordering — generally correct but not enforced in code
- For strict ordering requirements, set `BatchSize = 1` (severe performance impact)
- If ordering violations are observed, check if multiple publishers own the same partition

---

### FS-16: EventHub Batch Splitting Partial Failure

**Symptoms:**
- Error logs showing transport failures after partial batch send
- Same messages appearing as published AND in retry queue
- `retry_count` incrementing for messages that were already sent

**What happens:**
1. Message group exceeds EventHub's `MaxBatchSizeBytes` (1MB default)
2. Transport splits into multiple sub-batches
3. First sub-batch is sent successfully
4. Second sub-batch fails (timeout, broker error)
5. Exception propagates — `OutboxPublisherService` releases ALL messages in the group with `incrementRetry: true`
6. On next cycle, ALL messages (including already-sent ones) are re-leased and re-sent
7. If the failure is persistent, the entire group (including already-sent messages) is dead-lettered

**Risk:** Successfully-sent messages are dead-lettered because the group is treated atomically.

**Operator actions:**
- If seeing this pattern, reduce `BatchSize` or increase `MaxBatchSizeBytes`
- Ensure individual messages fit within a single EventHub batch
- Replay dead-lettered messages after fixing configuration
- Consider using smaller payload sizes to avoid batch splitting entirely

---

### FS-17: Kafka Flush Timeout / Ghost Writes

**Symptoms:**
- `TimeoutException` in logs from Kafka transport
- `outbox.publish.failures` incrementing
- Possible duplicate messages downstream

**What happens:**
1. `_producer.Flush(TimeSpan)` times out (15s default)
2. Remaining messages are still in librdkafka's internal queue
3. Transport throws `TimeoutException` — outbox retries entire batch
4. Meanwhile, librdkafka may deliver the original messages asynchronously
5. On retry, the same messages are produced again → duplicates

**KNOWN BUG:** The `Flush` call is synchronous and blocks the ThreadPool thread. The `CancellationToken` parameter is ignored, so graceful shutdown cannot interrupt a blocked flush.

**Operator actions:**
- If seeing flush timeouts, check broker latency and increase `SendTimeoutSeconds`
- Ensure `MessageTimeoutMs` < `SendTimeoutSeconds * 1000`
- Consumer idempotency is critical for this scenario
- Consider reducing `BatchSize` to decrease per-flush volume

---

### FS-18: Corrupted Headers in Outbox Rows

**Symptoms:**
- `JsonException` in logs during publish attempt
- Entire message group fails and retries
- Messages with corrupted headers eventually dead-lettered (taking the whole group with them)

**What happens:**
1. A message has invalid JSON in the `headers` column
2. `JsonSerializer.Deserialize` throws `JsonException` during `SendAsync`
3. Exception propagates — entire group treated as transport failure
4. After `MaxRetryCount`, message is dead-lettered
5. **All other messages in the same (TopicName, PartitionKey) group are also affected** because they are processed together

**KNOWN BUG:** A single corrupted-headers message can cause valid messages in the same group to be dead-lettered.

**Operator actions:**
- Find corrupted rows:
  ```sql
  -- PostgreSQL
  SELECT sequence_number, headers FROM outbox WHERE headers IS NOT NULL AND headers !~ '^\{';
  -- SQL Server
  SELECT SequenceNumber, Headers FROM dbo.Outbox WHERE Headers IS NOT NULL AND TRY_PARSE(Headers AS NVARCHAR(MAX)) IS NULL;
  ```
- Fix the headers in-place if possible:
  ```sql
  UPDATE outbox SET headers = NULL WHERE sequence_number = <id>;
  ```
- Replay dead-lettered messages after fixing

---

### FS-19: Health Check Reports Healthy But Publisher Is Stuck

**Symptoms:**
- Health check returns `Healthy`
- `outbox.messages.pending` > 0 and not decreasing
- No publish activity in logs

**Root cause:** The health check has a startup blind spot. If the publisher has **never** successfully heartbeated or polled (e.g., DB unreachable from process start), the staleness checks are skipped because `LastHeartbeatUtc == DateTimeOffset.MinValue`.

**KNOWN BUG:** A publisher whose DB connection fails from startup reports `Healthy` indefinitely.

**Operator actions:**
- Don't rely solely on the health check — also monitor `outbox.messages.pending` and `outbox.messages.published`
- Add a startup probe in Kubernetes that checks `outbox.messages.published > 0` or equivalent
- Add an external monitor that queries the outbox table directly:
  ```sql
  SELECT COUNT(*) FROM outbox WHERE created_at_utc < NOW() - INTERVAL '5 minutes';
  ```
  Alert if > 0 (messages older than 5 minutes should have been processed)

---

### FS-20: Loop Crash Escalation and Host Shutdown

**Symptoms:**
- Health check: `Degraded` (loop restarts > 0), then `Unhealthy`
- Log messages: "Outbox loop orchestration failed. Restart N/5"
- Eventually: "Max consecutive restarts exceeded. Stopping host."
- `IHostApplicationLifetime.StopApplication()` called

**What happens:**
1. A loop throws an unhandled exception that escapes the loop's catch block
2. Linked `CancellationTokenSource` cancels all other loops
3. `RunLoopsWithRestartAsync` increments restart counter
4. All loops restart after exponential backoff (2s, 4s, 8s, 16s, 32s)
5. If failure persists after 5 consecutive restarts, host is stopped

**KNOWN LIMITATION:** This path is only triggered by exceptions that escape loop catch blocks. Since all loops catch `Exception` and continue, this escalation is **effectively unreachable** for most failure modes (see [Known Bug #7](#known-bugs--limitations)).

**When it CAN trigger:**
- A bug in the publisher code itself (NullReferenceException, etc.)
- An exception thrown from `IOutboxEventHandler` callback (user code)
- `OutOfMemoryException` or `StackOverflowException`

**Operator actions:**
- Examine error logs for the root cause exception
- Fix the underlying bug and redeploy
- The host stopping is intentional — Kubernetes/orchestrator should restart the pod
- If this is from a user `IOutboxEventHandler`, ensure the handler doesn't throw

---

### FS-21: Database Failover / Connection Pool Exhaustion

**Symptoms:**
- Transient SQL errors in logs (deadlocks, timeouts)
- Brief processing pauses during failover
- Connection pool exhaustion errors under high concurrency

**What happens automatically:**
1. Built-in transient retry (3 attempts, 200ms linear backoff) handles brief blips
2. If retry budget is exhausted, error propagates to loop's catch block
3. Loop catches and retries on next interval

**KNOWN LIMITATION:** The retry backoff is **linear** (200ms, 400ms, 600ms), totaling only 600ms. Azure SQL failover typically takes 20-30 seconds. The retry budget will be exhausted on the first attempt, and all subsequent attempts rely on the outer loop retry (5s for publish loop, 10s for heartbeat).

**Operator actions:**
- For Azure SQL, expect ~30s disruption during failover — this is within the heartbeat timeout (30s)
- Monitor for connection pool exhaustion — if occurring, increase pool size
- Consider using a connection resiliency library (e.g., Polly) at the application level

---

### FS-22: Partition Hash Collision (INT_MIN Edge Case)

**Symptoms:**
- Specific partition keys never get processed
- Messages accumulate in outbox table for specific partition keys
- `retry_count` remains 0 (messages are never leased)

**Root cause:** `ABS(CHECKSUM(PartitionKey))` and `ABS(hashtext(partition_key))` overflow on `INT_MIN` (-2147483648), returning -2147483648 (negative). `negative % N` produces a negative partition ID that never matches any `partition_id` in the partitions table.

**KNOWN BUG:** Affects approximately 1 in 4 billion partition key values. Those messages are permanently stuck.

**Detection:**
```sql
-- PostgreSQL
SELECT * FROM outbox WHERE hashtext(partition_key) = -2147483648;

-- SQL Server
SELECT * FROM dbo.Outbox WHERE CHECKSUM(PartitionKey) = -2147483648;
```

**Operator actions:**
- If messages are found, manually update the partition key to a different value that doesn't hash to INT_MIN
- Apply the code fix: replace `ABS(hash)` with `(CAST(hash AS BIGINT) & 0x7FFFFFFF)`
- Re-deploy schema and publisher code

---

## Known Bugs & Limitations

The following issues were identified during code review and should be addressed:

### Critical (P0) — Fix Before Production

All P0 issues have been resolved.

| # | Issue | Component | Status |
|---|-------|-----------|--------|
| 1 | `ABS(INT_MIN)` hash overflow in both databases | Both stores | FIXED |
| 2 | SQL Server `UPDATE TOP` without `ORDER BY` in rebalance | SqlServerOutboxStore | FIXED |
| 3 | Kafka `Flush` blocks ThreadPool, ignores `CancellationToken` | KafkaOutboxTransport | FIXED |
| 4 | Kafka partial batch — ghost writes after flush timeout | KafkaOutboxTransport | FIXED |

### High (P1) — Should Fix Before Production

| # | Issue | Component | Status |
|---|-------|-----------|--------|
| 5 | All loops silently absorb exceptions — restart machinery unreachable for DB outages | OutboxPublisherService | OPEN — design trade-off: self-heal vs fail-fast |
| 6 | `DeadLetterAsync` has no `producerId` guard | IOutboxStore interface | FIXED |
| 7 | EventHub multi-sub-batch partial failure | EventHubOutboxTransport | FIXED — per-sub-batch timeout reset |
| 8 | PostgreSQL `RebalanceAsync` bypasses `ExecuteWithRetryAsync` | PostgreSqlOutboxStore | FIXED |
| 9 | No UNIQUE constraint on dead-letter `sequence_number` | Both schemas | OPEN — requires schema migration |
| 10 | Transport `DisposeAsync` disposes injected DI singleton | Both transports | FIXED |
| 11 | SQL Server rebalance under READ COMMITTED — phantom reads | SqlServerOutboxStore | OPEN — acceptable with rebalance cycle |
| 12 | Linear backoff in transient retry — too short for DB failover | Both DbHelpers | FIXED — exponential backoff |
| 13 | `WhenAll` exception can bypass `stoppingToken` check | OutboxPublisherService | FIXED |
| 14 | `JsonException` on corrupted headers propagates unhandled | Both transports | FIXED |

### Medium (P2)

| # | Issue | Component | Status |
|---|-------|-----------|--------|
| 15 | In-group message ordering not explicitly enforced | OutboxPublisherService | FIXED |
| 16 | Health check startup blind spot (never-heartbeated) | OutboxHealthCheck | FIXED |
| 17 | `hashtext()` vs `CHECKSUM()` — different hash distributions | Both stores | OPEN — architectural limitation |
| 18 | No index on `dead_letter.sequence_number` | Both schemas | OPEN — requires schema migration |
| 19 | EventHub timeout shared across all sub-batches | EventHubOutboxTransport | FIXED — timeout resets per sub-batch |
| 20 | No fail-fast validation on transport options | Both transports | OPEN — nice-to-have |
| 21 | Sweep can dead-letter successfully-sent messages | Both stores | OPEN — fundamental at-least-once limitation |
| 22 | `EventType` not propagated to Kafka headers | KafkaOutboxTransport | FIXED |

---

## Emergency Procedures

### Emergency: Stop All Publishing
```bash
# Option 1: Scale to zero (Kubernetes)
kubectl scale deployment outbox-publisher --replicas=0

# Option 2: Kill all publisher processes
pkill -f OutboxPublisher
```
Messages remain safely in the outbox table. Resume by scaling back up.

### Emergency: Drain Outbox Table Manually
**Only if publishers are permanently broken and messages must be delivered:**
```sql
-- Export pending messages
COPY (SELECT * FROM outbox ORDER BY event_datetime_utc, event_ordinal) TO '/tmp/outbox_export.csv' CSV HEADER;
-- Then process via a custom script that sends to the broker
```

### Emergency: Clear Stuck Leases
```sql
-- PostgreSQL
UPDATE outbox SET leased_until_utc = NULL, lease_owner = NULL WHERE leased_until_utc < clock_timestamp();

-- SQL Server
UPDATE dbo.Outbox SET LeasedUntilUtc = NULL, LeaseOwner = NULL WHERE LeasedUntilUtc < SYSUTCDATETIME();
```

### Emergency: Reset Partition Ownership
```sql
-- PostgreSQL
UPDATE outbox_partitions SET owner_producer_id = NULL, owned_since_utc = NULL, grace_expires_utc = NULL;

-- SQL Server
UPDATE dbo.OutboxPartitions SET OwnerProducerId = NULL, OwnedSinceUtc = NULL, GraceExpiresUtc = NULL;
```
The next rebalance cycle will reassign all partitions.

### Emergency: Force Dead-Letter Cleanup
```sql
-- PostgreSQL
DELETE FROM outbox_dead_letter WHERE dead_lettered_at_utc < clock_timestamp() - INTERVAL '30 days';

-- SQL Server
DELETE FROM dbo.OutboxDeadLetter WHERE DeadLetteredAtUtc < DATEADD(DAY, -30, SYSUTCDATETIME());
```

---

## Monitoring & Alerting Recommendations

### Critical Alerts (Page On-Call)

| Alert | Condition | Threshold |
|-------|-----------|-----------|
| Publisher Not Publishing | `outbox.messages.published` rate = 0 AND `outbox.messages.pending` > 0 | > 5 min |
| Dead Letters Appearing | `outbox.messages.dead_lettered` increment | Any (investigate immediately) |
| Host Shutdown | Log: "Max consecutive restarts exceeded" | Any occurrence |
| Outbox Table Depth | `outbox.messages.pending` | > 10,000 messages |

### Warning Alerts (Investigate Next Business Day)

| Alert | Condition | Threshold |
|-------|-----------|-----------|
| Circuit Breaker Open | `outbox.circuit_breaker.state_changes` | Open > 5 min continuously |
| Health Degraded | Health check != Healthy | > 10 min |
| Publish Latency | `outbox.publish.duration` p99 | > 10s |
| Stale Messages | Messages in outbox older than 10 min | Any |

### Dashboard Panels

1. **Messages Published/sec** — `rate(outbox.messages.published)`
2. **Messages Pending** — `outbox.messages.pending` gauge
3. **Publish Failures/sec** — `rate(outbox.publish.failures)`
4. **Circuit Breaker State** — `outbox.circuit_breaker.state_changes` with topic labels
5. **Dead Letters Total** — `outbox.messages.dead_lettered` counter
6. **Publish Duration p50/p95/p99** — `outbox.publish.duration` histogram
7. **Poll Batch Size** — `outbox.poll.batch_size` histogram (0 = no work or partition issue)
8. **Active Publishers** — count of `outbox_producers` rows with recent heartbeat
9. **Partition Distribution** — partitions per publisher (should be roughly equal)
