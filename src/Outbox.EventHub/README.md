# Outbox.EventHub

Azure EventHub transport for the outbox library. Implements `IOutboxTransport` using the [Azure.Messaging.EventHubs](https://learn.microsoft.com/en-us/dotnet/api/azure.messaging.eventhubs) SDK.

## Setup

```csharp
services.AddOutbox(configuration, outbox =>
{
    outbox.UsePostgreSql(connectionFactory);
    outbox.UseEventHub(opts =>
    {
        opts.ConnectionString = "Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...";
    });
});
```

The connection string must be namespace-level (no `EntityPath`). The Event Hub name comes from each message's `TopicName` column in the outbox table.

### Custom client factory

To use `DefaultAzureCredential` or any custom client creation logic, provide your own `EventHubClientFactory` delegate:

```csharp
outbox.UseEventHub()
    .UseClientFactory(eventHubName => new EventHubProducerClient(
        "my-namespace.servicebus.windows.net", eventHubName, new DefaultAzureCredential()));
```

The delegate receives the Event Hub name (from the message's `TopicName`) and returns a client. Clients are cached per topic name for the transport's lifetime.

## How it works

### Multi-Event Hub support

The transport manages a `ConcurrentDictionary<string, EventHubProducerClient>` internally. On the first send to a given topic name, it calls the `EventHubClientFactory` delegate to create a client and caches it. Subsequent sends to the same topic reuse the cached client. Clients sharing a namespace share the underlying AMQP connection.

### Message mapping

- **Body** — `OutboxMessage.Payload` (byte[])
- **Properties** — All entries from `OutboxMessage.Headers` plus `EventType` (always added last, overwriting any user-supplied value)
- **PartitionKey** — Set on `CreateBatchOptions`, not on individual events. All events in a `SendAsync` call share the same partition key.

### Batch sending

Uses the Azure SDK's `EventDataBatch` to accumulate events up to `MaxBatchSizeBytes`. When `TryAdd` returns false (batch full):

1. Send the current batch
2. Reset the send timeout
3. Create a new batch
4. Retry adding the event

If a single event can't fit in a fresh empty batch, `InvalidOperationException` is thrown—the message is permanently too large.

### Partial delivery

If an exception occurs after at least one sub-batch was successfully sent, a `PartialSendException` is thrown with accurate succeeded/failed sequence number lists.

### Fully async

Unlike the Kafka transport, EventHub's `SendAsync` is truly async—no thread blocking, no ThreadPool concerns. No ghost-write risk from timeout cancellation because `SendAsync` is atomic per batch.

### Disposal

`DisposeAsync` closes all cached `EventHubProducerClient` instances.

## Configuration

Bind from `"Outbox:EventHub"` in `IConfiguration`.

| Option | Default | Description |
|---|---|---|
| `ConnectionString` | `""` | Namespace-level EventHub connection string |
| `MaxBatchSizeBytes` | `1048576` | Batch size limit (0 = use EventHub default) |
| `SendTimeoutSeconds` | `15` | Per-sub-batch send timeout |

Options are captured once at construction—not hot-reloaded.

## Transport interceptors

Register interceptors that run on the `EventData` envelope after core interceptors:

```csharp
outbox.UseEventHub()
    .AddTransportInterceptor<MyEventHubInterceptor>();
```

## Testing note

`EventDataBatch` is a sealed Azure SDK type that can't be mocked. Full send-path coverage (batch fill, sub-batch split) requires integration tests against a real or emulated EventHub.
