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

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
