using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.Kafka;

public static class KafkaOutboxBuilderExtensions
{
    public static IOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Action<KafkaTransportOptions>? configure = null)
    {
        builder.Services.Configure<KafkaTransportOptions>(
            builder.Configuration.GetSection("Outbox:Kafka"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton<IProducer<string, string>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<KafkaTransportOptions>>().Value;
            var config = new ProducerConfig
            {
                BootstrapServers = opts.BootstrapServers,
                Acks = Enum.Parse<Acks>(opts.Acks, ignoreCase: true),
                EnableIdempotence = opts.EnableIdempotence,
                MessageSendMaxRetries = opts.MessageSendMaxRetries,
                RetryBackoffMs = opts.RetryBackoffMs,
                LingerMs = opts.LingerMs,
                MessageTimeoutMs = opts.MessageTimeoutMs,
            };
            return new ProducerBuilder<string, string>(config).Build();
        });

        builder.Services.TryAddSingleton<IOutboxTransport, KafkaOutboxTransport>();

        return builder;
    }

    public static IOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Func<IServiceProvider, IProducer<string, string>> producerFactory,
        Action<KafkaTransportOptions>? configure = null)
    {
        builder.Services.Configure<KafkaTransportOptions>(
            builder.Configuration.GetSection("Outbox:Kafka"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton(producerFactory);
        builder.Services.TryAddSingleton<IOutboxTransport, KafkaOutboxTransport>();

        return builder;
    }
}
