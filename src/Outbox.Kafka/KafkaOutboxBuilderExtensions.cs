// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;

namespace Outbox.Kafka;

public static class KafkaOutboxBuilderExtensions
{
    public static IKafkaOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Action<KafkaTransportOptions>? configure = null)
    {
        builder.Services.Configure<KafkaTransportOptions>(
            builder.Configuration.GetSection("Outbox:Kafka"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton<IProducer<string, byte[]>>(sp =>
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
                MessageTimeoutMs = opts.MessageTimeoutMs
            };

            return new ProducerBuilder<string, byte[]>(config).Build();
        });

        builder.Services.TryAddSingleton<IOutboxTransport, KafkaOutboxTransport>();

        return new KafkaOutboxBuilder(builder);
    }

    public static IKafkaOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Func<IServiceProvider, IProducer<string, byte[]>> producerFactory,
        Action<KafkaTransportOptions>? configure = null)
    {
        builder.Services.Configure<KafkaTransportOptions>(
            builder.Configuration.GetSection("Outbox:Kafka"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton(producerFactory);
        builder.Services.TryAddSingleton<IOutboxTransport, KafkaOutboxTransport>();

        return new KafkaOutboxBuilder(builder);
    }
}

internal sealed class KafkaOutboxBuilder : IKafkaOutboxBuilder
{
    private readonly IOutboxBuilder _inner;

    public KafkaOutboxBuilder(IOutboxBuilder inner) => _inner = inner;

    public IServiceCollection Services => _inner.Services;
    public Microsoft.Extensions.Configuration.IConfiguration Configuration => _inner.Configuration;

    public IKafkaOutboxBuilder AddTransportInterceptor<TInterceptor>()
        where TInterceptor : class, ITransportMessageInterceptor<Message<string, byte[]>>
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITransportMessageInterceptor<Message<string, byte[]>>, TInterceptor>());

        return this;
    }

    /// <summary>
    ///     Registers a transport interceptor using a factory delegate.
    ///     Unlike the generic overload, calling this multiple times will register multiple instances.
    /// </summary>
    public IKafkaOutboxBuilder AddTransportInterceptor(
        Func<IServiceProvider, ITransportMessageInterceptor<Message<string, byte[]>>> factory)
    {
        Services.AddSingleton(factory);

        return this;
    }

    // Delegate IOutboxBuilder methods — return this to preserve IKafkaOutboxBuilder for fluent chaining
    public IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure)
    {
        _inner.ConfigurePublisher(configure);

        return this;
    }

    public IOutboxBuilder ConfigureEvents<THandler>() where THandler : class, IOutboxEventHandler
    {
        _inner.ConfigureEvents<THandler>();

        return this;
    }

    public IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory)
    {
        _inner.ConfigureEvents(factory);

        return this;
    }

    public IOutboxBuilder AddMessageInterceptor<TInterceptor>() where TInterceptor : class, IOutboxMessageInterceptor
    {
        _inner.AddMessageInterceptor<TInterceptor>();

        return this;
    }

    public IOutboxBuilder AddMessageInterceptor(Func<IServiceProvider, IOutboxMessageInterceptor> factory)
    {
        _inner.AddMessageInterceptor(factory);

        return this;
    }
}
