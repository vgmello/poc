using System.Text.Json;
using Outbox.Publisher;
using Outbox.Shared;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OutboxDb")
    ?? throw new InvalidOperationException("ConnectionStrings__OutboxDb not set");
var bootstrapServers = Environment.GetEnvironmentVariable("Kafka__BootstrapServers")
    ?? throw new InvalidOperationException("Kafka__BootstrapServers not set");

var config = JsonSerializer.Deserialize<JsonElement>(
    await File.ReadAllTextAsync("appsettings.json"));

var options = new KafkaOutboxPublisherOptions
{
    BatchSize = config.GetProperty("BatchSize").GetInt32(),
    LeaseDurationSeconds = config.GetProperty("LeaseDurationSeconds").GetInt32(),
    MaxRetryCount = config.GetProperty("MaxRetryCount").GetInt32(),
    OnError = (context, ex) =>
        Console.Error.WriteLine($"[OutboxPublisher] {context}: {ex?.Message}")
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine($"[OutboxPublisher] Starting — bootstrap={bootstrapServers}, batch={options.BatchSize}");

// Wait for DB to be reachable
Console.WriteLine("[OutboxPublisher] Waiting for database...");
await using var initConn = await DbHelpers.OpenConnectionAsync(connectionString, cts.Token);
await initConn.CloseAsync();
Console.WriteLine("[OutboxPublisher] Database is reachable.");

await using var publisher = new KafkaOutboxPublisher(connectionString, bootstrapServers, options);
await publisher.StartAsync(cts.Token);

Console.WriteLine("[OutboxPublisher] Running. Press Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException) { }

// DisposeAsync (via `await using`) calls StopAsync internally — no explicit call needed.
Console.WriteLine("[OutboxPublisher] Shutting down...");
