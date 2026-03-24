// Copyright (c) OrgName. All rights reserved.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Outbox.Core.Observability;

internal sealed class OutboxInstrumentation : IDisposable
{
    public const string ActivitySourceName = "Outbox";
    public const string MeterName = "Outbox";

    public ActivitySource ActivitySource { get; }
    public Meter Meter { get; }

    public Counter<long> MessagesPublished { get; }
    public Counter<long> MessagesDeadLettered { get; }
    public Counter<long> CircuitBreakerStateChanges { get; }
    public Counter<long> PublishFailures { get; }

    public Histogram<double> PublishDuration { get; }
    public Histogram<double> PollDuration { get; }
    public Histogram<int> BatchSize { get; }
    public ObservableGauge<long> MessagesPending { get; private set; } = null!;

    public OutboxInstrumentation(IMeterFactory meterFactory)
    {
        ActivitySource = new ActivitySource(ActivitySourceName);
        Meter = meterFactory.Create(MeterName);

        MessagesPublished = Meter.CreateCounter<long>(
            "outbox.messages.published",
            description: "Number of messages successfully published");

        MessagesDeadLettered = Meter.CreateCounter<long>(
            "outbox.messages.dead_lettered",
            description: "Number of messages moved to dead letter table");

        CircuitBreakerStateChanges = Meter.CreateCounter<long>(
            "outbox.circuit_breaker.state_changes",
            description: "Number of circuit breaker state transitions");

        PublishFailures = Meter.CreateCounter<long>(
            "outbox.publish.failures",
            description: "Number of failed publish attempts");

        PublishDuration = Meter.CreateHistogram<double>(
            "outbox.publish.duration",
            unit: "ms",
            description: "Duration of publish operations in milliseconds");

        PollDuration = Meter.CreateHistogram<double>(
            "outbox.poll.duration",
            unit: "ms",
            description: "Duration of poll operations in milliseconds");

        BatchSize = Meter.CreateHistogram<int>(
            "outbox.poll.batch_size",
            description: "Number of messages leased per poll");
    }

    private long _pendingCount;
    private int _pendingGaugeRegistered;

    public void RegisterPendingGauge()
    {
        if (Interlocked.CompareExchange(ref _pendingGaugeRegistered, 1, 0) != 0)
            return;

        MessagesPending = Meter.CreateObservableGauge(
            "outbox.messages.pending",
            () => Volatile.Read(ref _pendingCount),
            description: "Number of messages waiting to be published");
    }

    public void UpdatePendingCount(long count) =>
        Volatile.Write(ref _pendingCount, count);

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
