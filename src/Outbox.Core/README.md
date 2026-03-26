# Outbox.Core

The engine and abstractions for the outbox pattern library. Contains the publisher service, circuit breaker, health checks, observability, DI builder, and all public interfaces. No infrastructure code—database and broker specifics live in separate packages.

## Packages that depend on this

- `Outbox.Kafka` — Kafka transport
- `Outbox.EventHub` — Azure EventHub transport
- `Outbox.PostgreSQL` — PostgreSQL store
- `Outbox.SqlServer` — SQL Server store

## Key abstractions

### IOutboxStore

The contract between the publisher engine and any database backend. Every method is async and cancellation-aware.

| Method | Purpose |
|---|---|
| `RegisterPublisherAsync` | Register this publisher instance, returns a `publisherId` |
| `UnregisterPublisherAsync` | Remove registration and release partition ownership |
| `LeaseBatchAsync` | Claim a batch of messages from owned partitions |
| `DeletePublishedAsync` | Remove successfully sent messages (checks `lease_owner`) |
| `ReleaseLeaseAsync` | Clear lease, optionally increment `retry_count` |
| `DeadLetterAsync` | Atomically move messages to the dead-letter table |
| `HeartbeatAsync` | Update heartbeat and clear grace periods |
| `GetTotalPartitionsAsync` | Total partition count (cached 60s) |
| `GetOwnedPartitionsAsync` | Partitions assigned to this publisher |
| `RebalanceAsync` | Fair-share partition redistribution |
| `ClaimOrphanPartitionsAsync` | Claim unowned partitions |
| `SweepDeadLettersAsync` | Background sweep of exhausted-retry messages |
| `GetPendingCountAsync` | Count of pending messages (for metrics) |

### IOutboxTransport

Single-method interface for sending a pre-ordered group of messages to a broker.

```csharp
Task SendAsync(string topicName, string partitionKey,
               IReadOnlyList<OutboxMessage> messages,
               CancellationToken cancellationToken);
```

Transports must publish all messages or throw. For partial delivery, throw `PartialSendException` with succeeded and failed sequence number lists. Implements `IAsyncDisposable` but must not dispose DI singletons.

### IOutboxEventHandler

Observer callbacks for lifecycle events. All methods have default implementations returning `Task.CompletedTask`. Exceptions are caught and logged—they never affect message fate.

| Callback | When it fires |
|---|---|
| `OnMessagePublishedAsync` | After successful send, before delete |
| `OnPublishFailedAsync` | After transport failure, after lease release |
| `OnMessageDeadLetteredAsync` | After dead-lettering |
| `OnCircuitBreakerStateChangedAsync` | After circuit state change |
| `OnRebalanceAsync` | After partition rebalance |

### IOutboxMessageInterceptor

Transform messages after store retrieval, before transport dispatch. Can mutate `Payload`, `Headers`, and `PayloadContentType`. Routing fields are read-only.

```csharp
bool AppliesTo(OutboxMessage message);
ValueTask InterceptAsync(OutboxMessageContext context, CancellationToken ct);
```

### ITransportMessageInterceptor\<TMessage\>

Transform the transport-specific envelope (e.g., Kafka `Message<string, byte[]>` or EventHub `EventData`).

### IDeadLetterManager

Administrative interface for dead-letter operations. Not used by the publisher—intended for operational tooling.

| Method | Purpose |
|---|---|
| `GetAsync` | Paginated read |
| `ReplayAsync` | Move back to outbox (resets `retry_count` to 0) |
| `PurgeAsync` | Permanently delete specific messages |
| `PurgeAllAsync` | Truncate the dead-letter table |

## Models

### OutboxMessage

Immutable record representing a message in the outbox.

| Field | Type | Description |
|---|---|---|
| `SequenceNumber` | `long` | DB-assigned monotonic identity |
| `TopicName` | `string` | Broker topic/queue name |
| `PartitionKey` | `string` | Determines logical partition ownership |
| `EventType` | `string` | Domain event type, sent as a header |
| `Headers` | `Dictionary<string, string>?` | Optional headers |
| `Payload` | `byte[]` | Raw message bytes |
| `PayloadContentType` | `string` | MIME type (e.g., `application/json`) |
| `EventDateTimeUtc` | `DateTimeOffset` | Primary ordering key |
| `EventOrdinal` | `short` | Tie-breaker within same timestamp |
| `RetryCount` | `int` | Delivery attempts so far |
| `CreatedAtUtc` | `DateTimeOffset` | Row insertion time |

### DeadLetteredMessage

Extends `OutboxMessage` fields with `DeadLetterSeq`, `DeadLetteredAtUtc`, and `LastError`.

### PartialSendException

Signals partial batch delivery. Carries `SucceededSequenceNumbers` and `FailedSequenceNumbers` so the publisher can handle the split.

## Engine

### OutboxPublisherService

A sealed `BackgroundService` that runs five concurrent loops:

| Loop | Interval | Purpose |
|---|---|---|
| Publish | Adaptive (100ms–5s) | Lease → Send → Delete core path |
| Heartbeat | 10s | Keep publisher alive, clear grace periods |
| Rebalance | 30s | Fair-share partition distribution |
| Orphan sweep | 60s | Claim unowned partitions |
| Dead-letter sweep | 60s | Quarantine exhausted-retry messages |

If any loop exits, all others are cancelled. After 5 consecutive restarts without 30s of healthy operation, the service calls `StopApplication()`.

### TopicCircuitBreaker

Per-topic circuit breaker with three states: Closed, Open, HalfOpen. Prevents retry-count burn during broker outages by releasing messages without incrementing `retry_count` when open.

## Health check

Registered under the name `"outbox"` with tags `["outbox", "ready"]`. Reports Unhealthy when the publish loop is down or heartbeat/polls are stale. Reports Degraded when circuits are open or loops have restarted.

## Observability

Meter `"Outbox"` with counters (`messages.published`, `messages.dead_lettered`, `publish.failures`, `circuit_breaker.state_changes`), histograms (`publish.duration`, `poll.duration`, `poll.batch_size`), and an observable gauge (`messages.pending`).

ActivitySource `"Outbox"` with one activity per `(topic, partitionKey)` group.

## Builder API

```csharp
services.AddOutbox(configuration, outbox =>
{
    outbox.ConfigurePublisher(opts => opts.BatchSize = 50);
    outbox.UsePostgreSql(connectionFactory);   // from Outbox.PostgreSQL
    outbox.UseKafka();                          // from Outbox.Kafka
    outbox.ConfigureEvents<MyEventHandler>();
    outbox.AddMessageInterceptor<MyInterceptor>();
});
```

`AddOutbox` registers the publisher service, health check, instrumentation, and configuration validation. Store and transport are registered by the respective extension packages.

## Configuration

Bind from `"Outbox:Publisher"` in `IConfiguration`. Supports hot-reload via `IOptionsMonitor`.

| Option | Default |
|---|---|
| `BatchSize` | 100 |
| `LeaseDurationSeconds` | 45 |
| `MaxRetryCount` | 5 |
| `MinPollIntervalMs` | 100 |
| `MaxPollIntervalMs` | 5000 |
| `HeartbeatIntervalMs` | 10000 |
| `HeartbeatTimeoutSeconds` | 30 |
| `PartitionGracePeriodSeconds` | 60 |
| `RebalanceIntervalMs` | 30000 |
| `OrphanSweepIntervalMs` | 60000 |
| `DeadLetterSweepIntervalMs` | 60000 |
| `CircuitBreakerFailureThreshold` | 3 |
| `CircuitBreakerOpenDurationSeconds` | 30 |
