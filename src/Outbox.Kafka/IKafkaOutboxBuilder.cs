// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.Kafka;

public interface IKafkaOutboxBuilder : IOutboxBuilder
{
    IKafkaOutboxBuilder AddTransportInterceptor<TInterceptor>()
        where TInterceptor : class, ITransportMessageInterceptor<Message<string, byte[]>>;

    IKafkaOutboxBuilder AddTransportInterceptor(
        Func<IServiceProvider, ITransportMessageInterceptor<Message<string, byte[]>>> factory);
}
