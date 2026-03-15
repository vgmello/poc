using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Outbox.Samples.Shared;

public sealed class KafkaConsumer(IConfiguration configuration, ILogger<KafkaConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var bootstrapServers = configuration["Outbox:Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Outbox:Kafka:BootstrapServers is not configured.");

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"outbox-sample-consumer-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe("orders");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(stoppingToken);
                if (result is null) continue;

                logger.LogInformation(
                    "Consumer: Received event from {Topic}/{PartitionKey} — {Payload}",
                    result.Topic, result.Message.Key, result.Message.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            consumer.Close();
        }
    }
}
