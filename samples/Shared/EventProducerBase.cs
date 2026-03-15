using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Outbox.Samples.Shared;

public abstract class EventProducerBase(ILogger logger) : BackgroundService
{
    private static readonly string[] PartitionKeys =
        Enumerable.Range(1, 8).Select(i => $"customer-{i}").ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var partitionKey = PartitionKeys[Random.Shared.Next(PartitionKeys.Length)];
            var count = Random.Shared.Next(3, 6); // 3 to 5 inclusive

            for (var i = 0; i < count; i++)
            {
                var evt = SampleEvent.Generate(partitionKey);
                var payload = evt.ToJson();

                await InsertOutboxRowAsync(
                    topicName: "orders",
                    partitionKey: partitionKey,
                    eventType: nameof(SampleEvent),
                    headers: null,
                    payload: payload,
                    ct: stoppingToken);
            }

            logger.LogInformation(
                "EventProducer: Inserted {Count} events for {PartitionKey}",
                count, partitionKey);

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    protected abstract Task InsertOutboxRowAsync(
        string topicName,
        string partitionKey,
        string eventType,
        IReadOnlyDictionary<string, string>? headers,
        string payload,
        CancellationToken ct);
}
