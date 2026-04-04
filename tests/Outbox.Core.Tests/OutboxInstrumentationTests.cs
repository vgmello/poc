// Copyright (c) OrgName. All rights reserved.

using System.Diagnostics.Metrics;
using Outbox.Core.Observability;
using Xunit;

namespace Outbox.Core.Tests;

public sealed class OutboxInstrumentationTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public void Constructor_CreatesMeter_WithCorrectName()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory);
        Assert.Equal("Outbox", instrumentation.Meter.Name);
    }

    [Fact]
    public void Constructor_WithGroupName_CreatesMeter_WithPrefixedName()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory, "payments");
        Assert.Equal("payments.Outbox", instrumentation.Meter.Name);
    }

    [Fact]
    public void Constructor_SetsGroupNameProperty()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory, "payments");
        Assert.Equal("payments", instrumentation.GroupName);
    }

    [Fact]
    public void Constructor_WithoutGroupName_GroupNameIsNull()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory);
        Assert.Null(instrumentation.GroupName);
    }

    [Fact]
    public void RegisterPendingGauge_IsIdempotent()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory);
        instrumentation.RegisterPendingGauge();
        var gauge1 = instrumentation.MessagesPending;

        instrumentation.RegisterPendingGauge();
        var gauge2 = instrumentation.MessagesPending;

        Assert.Same(gauge1, gauge2);
    }

    [Fact]
    public void UpdatePendingCount_UpdatesValue()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory);
        instrumentation.RegisterPendingGauge();

        instrumentation.UpdatePendingCount(42);

        using var listener = new MeterListener();
        long? observedValue = null;
        listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "outbox.messages.pending")
                listener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            observedValue = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(42L, observedValue);
    }

    [Fact]
    public void Counters_CreatedWithCorrectNames()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory);
        Assert.Equal("outbox.messages.published", instrumentation.MessagesPublished.Name);
        Assert.Equal("outbox.messages.dead_lettered", instrumentation.MessagesDeadLettered.Name);
        Assert.Equal("outbox.circuit_breaker.state_changes", instrumentation.CircuitBreakerStateChanges.Name);
        Assert.Equal("outbox.publish.failures", instrumentation.PublishFailures.Name);
    }

    [Fact]
    public void Histograms_CreatedWithCorrectNames()
    {
        using var instrumentation = new OutboxInstrumentation(_meterFactory);
        Assert.Equal("outbox.publish.duration", instrumentation.PublishDuration.Name);
        Assert.Equal("outbox.poll.duration", instrumentation.PollDuration.Name);
        Assert.Equal("outbox.poll.batch_size", instrumentation.BatchSize.Name);
    }
}
