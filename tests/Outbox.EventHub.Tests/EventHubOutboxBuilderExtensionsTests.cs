// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.EventHub.Tests;

public class EventHubOutboxBuilderExtensionsTests
{
    [Fact]
    public void UseEventHub_WithConfigure_RegistersIOutboxTransport()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:EventHub:ConnectionString"] =
                    "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=abc="
            })
            .Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.UseEventHub(configure: opts =>
            {
                opts.MaxBatchSizeBytes = 256_000;
            });
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IOutboxTransport));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(EventHubOutboxTransport), descriptor.ImplementationType);
    }

    [Fact]
    public void UseEventHub_NoConfigure_RegistersIOutboxTransport()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.UseEventHub();
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IOutboxTransport));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(EventHubOutboxTransport), descriptor.ImplementationType);
    }

    [Fact]
    public void UseEventHub_RegistersDefaultClientFactory()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:EventHub:ConnectionString"] =
                    "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=abc="
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        services.AddOutbox(config, outbox =>
        {
            outbox.UseEventHub();
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetService<EventHubClientFactory>();
        Assert.NotNull(factory);
    }

    [Fact]
    public void UseEventHub_ResolvesTransport()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:EventHub:ConnectionString"] =
                    "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=abc="
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        services.AddOutbox(config, outbox =>
        {
            outbox.UseEventHub();
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var transport = provider.GetService<IOutboxTransport>();
        Assert.NotNull(transport);
        Assert.IsType<EventHubOutboxTransport>(transport);
    }

    [Fact]
    public void UseClientFactory_ReplacesDefaultFactory()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:EventHub:ConnectionString"] =
                    "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=abc="
            })
            .Build();

        var mockClient = Substitute.For<EventHubProducerClient>();
        EventHubClientFactory customFactory = _ => mockClient;

        services.AddOutbox(config, outbox =>
        {
            outbox.UseEventHub()
                .UseClientFactory(customFactory);
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<EventHubClientFactory>();
        Assert.Same(customFactory, factory);
    }

    [Fact]
    public void EventHubTransportOptionsValidator_Fails_WhenConnectionStringMissing()
    {
        var publisherOptions = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        publisherOptions.Get(Options.DefaultName).Returns(new OutboxPublisherOptions());

        var sut = new EventHubTransportOptionsValidator(publisherOptions);

        var result = sut.Validate(Options.DefaultName, new EventHubTransportOptions
        {
            ConnectionString = ""
        });

        Assert.True(result.Failed);
        Assert.Contains("ConnectionString", result.FailureMessage);
    }
}
