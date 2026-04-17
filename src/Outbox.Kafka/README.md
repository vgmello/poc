# Outbox.Kafka

Kafka transport for the outbox library, built on [Confluent.Kafka](https://github.com/confluentinc/confluent-kafka-dotnet). Implements `IOutboxTransport` to deliver outbox messages to Kafka topics.

## Setup

```csharp
services.AddOutbox(configuration, outbox =>
{
    outbox.UsePostgreSql(connectionFactory);
    outbox.UseKafka(opts =>
    {
        opts.BootstrapServers = "localhost:9092";
        opts.MaxBatchSizeBytes = 1_048_576;
    });
});
```

Or bring your own producer:

```csharp
outbox.UseKafka(
    producerFactory: sp => new ProducerBuilder<string, byte[]>(config).Build(),
    configure: opts => opts.SendTimeoutSeconds = 30
);
```

## How it works

### Message mapping

- **Key** — `partitionKey` (string)
- **Value** — `OutboxMessage.Payload` (byte[])
- **Headers** — All entries from `OutboxMessage.Headers` plus `EventType` (always added last, overwriting any user-supplied value)

### Sub-batch splitting

Messages are split into sub-batches when the estimated size exceeds `MaxBatchSizeBytes` (default 1 MB). The size estimate includes the partition key, payload, event type, headers, and a conservative framing overhead (100 bytes per message, 20 bytes per header).

A single oversized message is never rejected at the splitting stage—it's sent in its own sub-batch and will fail via a Kafka delivery report if it exceeds the broker's `max.request.size`.

### Produce and flush

Messages are submitted via `IProducer.Produce()` (non-blocking, callback-based). A dedicated long-running thread runs a flush loop to drain delivery reports:

```csharp
Task.Factory.StartNew(() => {
    while (!tcs.Task.IsCompleted)
    {
        cts.Token.ThrowIfCancellationRequested();
        _producer.Flush(TimeSpan.FromMilliseconds(100));
    }
}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
```

`TaskCreationOptions.LongRunning` prevents ThreadPool starvation when Kafka is under sustained backpressure.

### Partial delivery

Delivery callbacks track succeeded and failed sequence numbers independently. If some messages succeed and others fail within a sub-batch, a `PartialSendException` is thrown. When sub-batches span multiple flushes, the transport aggregates results across all sub-batches before reporting.

### Disposal

`DisposeAsync` does **not** dispose the producer—it's owned by the DI container. It performs a best-effort 5-second flush on shutdown.

## Configuration

Bind from `"Outbox:Kafka"` in `IConfiguration`.

| Option | Default | Description |
|---|---|---|
| `BootstrapServers` | `""` | Kafka broker addresses |
| `Acks` | `"All"` | Acknowledgment level |
| `MessageSendMaxRetries` | `3` | Kafka-internal retries |
| `RetryBackoffMs` | `500` | Kafka-internal retry backoff |
| `LingerMs` | `5` | Batching linger |
| `MessageTimeoutMs` | `15000` | Kafka message timeout |
| `SendTimeoutSeconds` | `15` | Application-level flush timeout |
| `MaxBatchSizeBytes` | `1048576` | Sub-batch splitting threshold |

`SendTimeoutSeconds` and `MaxBatchSizeBytes` are captured once at construction—not hot-reloaded.

The Kafka producer is always built with `EnableIdempotence = true`. This is a hard requirement: the library's partial-send retry logic relies on idempotence to guarantee delivery-report successes form a contiguous prefix. The matching property on `KafkaTransportOptions` is `[Obsolete]` and ignored.

## Transport interceptors

Register interceptors that run on the Kafka `Message<string, byte[]>` envelope after core interceptors:

```csharp
outbox.UseKafka()
    .AddTransportInterceptor<MyKafkaInterceptor>();
```

## Known limitations

### Flush blocks a thread

Confluent.Kafka's `Flush()` is synchronous. The mitigation (long-running task) prevents ThreadPool starvation but still blocks a dedicated OS thread under sustained backpressure.

### Ghost writes after flush timeout

If the flush timeout fires before all delivery reports arrive, some messages may have been acknowledged by Kafka but are treated as failed by the publisher. They'll be retried and potentially dead-lettered despite successful delivery. Tune `SendTimeoutSeconds` to realistic Kafka latency for your environment.

See `docs/known-limitations.md` for details.
