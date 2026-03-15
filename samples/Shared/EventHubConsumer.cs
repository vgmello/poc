using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Outbox.Samples.Shared;

public sealed class EventHubConsumer(IConfiguration configuration, ILogger<EventHubConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // EventHub emulator is slow to start
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        var connectionString = configuration.GetConnectionString("EventHub")
            ?? throw new InvalidOperationException("ConnectionStrings:EventHub is not configured.");
        var eventHubName = configuration["Outbox:EventHub:EventHubName"]
            ?? throw new InvalidOperationException("Outbox:EventHub:EventHubName is not configured.");

        await using var consumer = new EventHubConsumerClient(
            EventHubConsumerClient.DefaultConsumerGroupName,
            connectionString,
            eventHubName);

        try
        {
            await foreach (var partitionEvent in consumer.ReadEventsAsync(stoppingToken))
            {
                var payload = partitionEvent.Data.EventBody.ToString();
                var partitionKey = partitionEvent.Data.PartitionKey ?? "(none)";

                logger.LogInformation(
                    "Consumer: Received event from {EventHub}/{PartitionKey} — {Payload}",
                    eventHubName, partitionKey, payload);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }
}
