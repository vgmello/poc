using System.Text.Json;
using Npgsql;
using Outbox.Shared;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OutboxDb")
    ?? throw new InvalidOperationException("ConnectionStrings__OutboxDb not set");

// Load config from appsettings.json
var config = JsonSerializer.Deserialize<JsonElement>(
    await File.ReadAllTextAsync("appsettings.json"));
var topicName = config.GetProperty("TopicName").GetString()!;
var batchSize = config.GetProperty("BatchSize").GetInt32();
var intervalSeconds = config.GetProperty("IntervalSeconds").GetInt32();
var partitionKeyCount = config.GetProperty("PartitionKeyCount").GetInt32();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine($"[EventProducer] Starting — topic={topicName}, batch={batchSize}, interval={intervalSeconds}s, keys={partitionKeyCount}");

// Wait for DB to be reachable
Console.WriteLine("[EventProducer] Waiting for database...");
await using var initConn = await DbHelpers.OpenConnectionAsync(connectionString, cts.Token);
Console.WriteLine("[EventProducer] Database is reachable.");

var random = new Random();
string[] eventTypes = ["OrderCreated", "OrderUpdated"];

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        await using var conn = await DbHelpers.OpenConnectionAsync(connectionString, cts.Token);

        var batchTimestamp = DateTime.UtcNow;
        for (int i = 0; i < batchSize; i++)
        {
            var partitionKey = $"customer-{random.Next(1, partitionKeyCount + 1)}";
            var eventType = eventTypes[random.Next(eventTypes.Length)];
            var payload = JsonSerializer.Serialize(new
            {
                orderId = Guid.NewGuid().ToString("N"),
                amount = Math.Round(random.NextDouble() * 1000, 2),
                timestamp = DateTime.UtcNow.ToString("O")
            });

            await DbHelpers.InsertOutboxRowAsync(
                conn, topicName, partitionKey, eventType, null, payload,
                batchTimestamp, (short)i, cts.Token);
        }

        Console.WriteLine($"[EventProducer] Inserted {batchSize} events at {DateTime.UtcNow:HH:mm:ss.fff}");
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token);
    }
    catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[EventProducer] Error: {ex.Message}");
        await Task.Delay(2000, cts.Token);
    }
}

Console.WriteLine("[EventProducer] Shutting down.");
