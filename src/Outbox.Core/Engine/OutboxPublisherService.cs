using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Engine;

internal sealed class OutboxPublisherService : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxInstrumentation _instrumentation;

    public OutboxPublisherService(
        IOutboxStore store,
        IOutboxTransport transport,
        IOutboxEventHandler eventHandler,
        IOptionsMonitor<OutboxPublisherOptions> options,
        ILogger<OutboxPublisherService> logger,
        OutboxInstrumentation instrumentation)
    {
        _store = store;
        _transport = transport;
        _eventHandler = eventHandler;
        _options = options;
        _logger = logger;
        _instrumentation = instrumentation;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stub — full implementation in Task 8
        return Task.CompletedTask;
    }
}
