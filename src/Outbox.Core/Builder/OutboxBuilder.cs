using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Abstractions;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

internal sealed class OutboxBuilder : IOutboxBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }

    public OutboxBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }

    public IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    public IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler
    {
        Services.AddSingleton<IOutboxEventHandler, THandler>();
        return this;
    }

    public IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory)
    {
        Services.AddSingleton(factory);
        return this;
    }
}
