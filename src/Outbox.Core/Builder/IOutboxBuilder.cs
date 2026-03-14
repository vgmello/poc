using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Abstractions;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
    IConfiguration Configuration { get; }

    IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure);
    IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler;
    IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory);
}
