// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Abstractions;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
    IConfiguration Configuration { get; }
    string? GroupName { get; }

    IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure);

    IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler;

    IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory);

    /// <summary>
    ///     Registers a message interceptor. Interceptors run in the order they are registered.
    ///     The first registered interceptor runs first; its mutations are visible to subsequent interceptors.
    /// </summary>
    IOutboxBuilder AddMessageInterceptor<TInterceptor>()
        where TInterceptor : class, IOutboxMessageInterceptor;

    /// <summary>
    ///     Registers a message interceptor using a factory delegate. Interceptors run in the
    ///     order they are registered; mutations are visible to subsequent interceptors.
    ///     Unlike the generic overload, calling this multiple times with the same factory
    ///     will register multiple interceptor instances. Use this when you need multiple
    ///     instances of the same type with different configuration.
    /// </summary>
    IOutboxBuilder AddMessageInterceptor(Func<IServiceProvider, IOutboxMessageInterceptor> factory);
}
