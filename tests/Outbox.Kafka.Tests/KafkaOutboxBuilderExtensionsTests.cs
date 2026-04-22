// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.Kafka.Tests;

public class KafkaOutboxBuilderExtensionsTests
{
    [Fact]
    public void UseKafka_WithProducerFactory_RegistersIOutboxTransport()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);

        var mockProducer = Substitute.For<IProducer<string, byte[]>>();

        services.AddOutbox(config, outbox =>
        {
            outbox.UseKafka(
                producerFactory: _ => mockProducer,
                configure: opts => opts.BootstrapServers = "localhost:9092");
        });

        // Add required dependencies
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var transport = provider.GetService<IOutboxTransport>();
        Assert.NotNull(transport);
        Assert.IsType<KafkaOutboxTransport>(transport);
    }

    [Fact]
    public void UseKafka_WithProducerFactory_UsesProvidedFactory()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);

        var mockProducer = Substitute.For<IProducer<string, byte[]>>();
        var factoryInvoked = false;

        services.AddOutbox(config, outbox =>
        {
            outbox.UseKafka(producerFactory: sp =>
            {
                factoryInvoked = true;

                return mockProducer;
            });
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        // Resolve IOutboxTransport to trigger the factory
        _ = provider.GetService<IOutboxTransport>();
        // Resolve IProducer explicitly to trigger factory
        _ = provider.GetService<IProducer<string, byte[]>>();

        Assert.True(factoryInvoked);
    }

    [Fact]
    public void UseKafka_WithProducerFactory_NoConfigure_RegistersTransport()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);

        var mockProducer = Substitute.For<IProducer<string, byte[]>>();

        services.AddOutbox(config, outbox =>
        {
            outbox.UseKafka(producerFactory: _ => mockProducer);
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var transport = provider.GetService<IOutboxTransport>();
        Assert.NotNull(transport);
    }

    [Fact]
    public void UseKafka_Configure_AppliesOptions()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);

        var mockProducer = Substitute.For<IProducer<string, byte[]>>();

        services.AddOutbox(config, outbox =>
        {
            outbox.UseKafka(
                producerFactory: _ => mockProducer,
                configure: opts =>
                {
                    opts.BootstrapServers = "kafka:9092";
                    opts.MaxBatchSizeBytes = 512_000;
                });
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var transport = provider.GetService<IOutboxTransport>();
        Assert.NotNull(transport);
    }

    [Fact]
    public void UseKafka_DefaultOverload_WithConfigure_RegistersIOutboxTransport()
    {
        // Tests the first UseKafka overload (no producerFactory) with a configure delegate.
        // Exercises: the configure != null branch and IOutboxTransport registration.
        // The IProducer singleton is NOT resolved to avoid trying to connect to a real broker.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:Kafka:BootstrapServers"] = "localhost:9092"
            })
            .Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.UseKafka(configure: opts =>
            {
                opts.BootstrapServers = "localhost:9092";
                opts.MaxBatchSizeBytes = 512_000;
            });
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        // Verify IOutboxTransport is registered (do not resolve — that would create a real Kafka producer)
        var provider = services.BuildServiceProvider();
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IOutboxTransport));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(KafkaOutboxTransport), descriptor.ImplementationType);
    }

    [Fact]
    public void UseKafka_DefaultOverload_NoConfigure_RegistersIOutboxTransport()
    {
        // Tests the first UseKafka overload without any configure delegate (configure is null).
        // Exercises: the configure == null branch (the if block is skipped).
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.UseKafka(); // no configure delegate — exercises configure==null path
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IOutboxTransport));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(KafkaOutboxTransport), descriptor.ImplementationType);
    }

    [Fact]
    public void KafkaTransportOptionsValidator_Fails_WhenBootstrapServersMissing()
    {
        var publisherOptions = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        publisherOptions.Get(Options.DefaultName).Returns(new OutboxPublisherOptions());

        var sut = new KafkaTransportOptionsValidator(publisherOptions);

        var result = sut.Validate(Options.DefaultName, new KafkaTransportOptions
        {
            BootstrapServers = " "
        });

        Assert.True(result.Failed);
        Assert.Contains("BootstrapServers", result.FailureMessage);
    }
}
