// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.EventHub;

public interface IEventHubOutboxBuilder : IOutboxBuilder
{
    IEventHubOutboxBuilder AddTransportInterceptor<TInterceptor>()
        where TInterceptor : class, ITransportMessageInterceptor<EventData>;

    IEventHubOutboxBuilder AddTransportInterceptor(
        Func<IServiceProvider, ITransportMessageInterceptor<EventData>> factory);

    IEventHubOutboxBuilder UseClientFactory(EventHubClientFactory factory);
}
