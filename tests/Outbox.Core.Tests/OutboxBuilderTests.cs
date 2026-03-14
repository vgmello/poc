using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.Core.Tests;

public class OutboxBuilderTests
{
    [Fact]
    public void AddOutbox_RegistersHostedService()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigurePublisher(o => o.BatchSize = 50);
        });

        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();
        Assert.Contains(hostedServices, s => s.GetType().Name == "OutboxPublisherService");
    }

    [Fact]
    public void AddOutbox_BindsPublisherOptionsFromConfiguration()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:Publisher:BatchSize"] = "200",
                ["Outbox:Publisher:MaxRetryCount"] = "10",
            })
            .Build();

        services.AddOutbox(config, _ => { });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>();
        Assert.Equal(200, options.CurrentValue.BatchSize);
        Assert.Equal(10, options.CurrentValue.MaxRetryCount);
    }

    [Fact]
    public void ConfigurePublisher_OverridesConfigurationValues()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:Publisher:BatchSize"] = "200",
            })
            .Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigurePublisher(o => o.BatchSize = 500);
        });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>();
        Assert.Equal(500, options.CurrentValue.BatchSize);
    }

    [Fact]
    public void ConfigureEvents_RegistersCustomHandler()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigureEvents<TestEventHandler>();
        });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IOutboxEventHandler>();
        Assert.IsType<TestEventHandler>(handler);
    }

    [Fact]
    public void ConfigureEvents_WithFactory_RegistersHandler()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        var mockHandler = Substitute.For<IOutboxEventHandler>();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigureEvents(sp => mockHandler);
        });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IOutboxEventHandler>();
        Assert.Same(mockHandler, handler);
    }

    [Fact]
    public void AddOutbox_WithoutEventHandler_RegistersNoOpHandler()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, _ => { });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IOutboxEventHandler>();
        Assert.NotNull(handler);
    }

    private sealed class TestEventHandler : IOutboxEventHandler { }
}
