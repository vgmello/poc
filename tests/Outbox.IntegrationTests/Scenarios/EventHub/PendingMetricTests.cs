// Copyright (c) OrgName. All rights reserved.

using System.Diagnostics.Metrics;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

[Collection(InfrastructureCollection.Name)]
public class PendingMetricTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public PendingMetricTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PendingGauge_ReflectsOutboxDepth()
    {
        var eventHub = EventHubTestHelper.CheckoutHub();
        var partitionKey = $"pk-{Guid.NewGuid():N}";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Block transport so messages accumulate
        var (host, transport) = EventHubTestHelper.BuildEventHubPublisherHost(
            _infra.ConnectionString, _infra.EventHubConnectionString);
        transport.SetFailing(true);

        // Set up a MeterListener to capture the pending gauge metric
        long capturedPendingValue = -1;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "outbox.messages.pending")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "outbox.messages.pending")
                Interlocked.Exchange(ref capturedPendingValue, measurement);
        });
        meterListener.Start();

        try
        {
            await host.StartAsync();

            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, eventHub, partitionKey);

            // Wait for at least one heartbeat cycle to update pending count
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                meterListener.RecordObservableInstruments();
                var value = Interlocked.Read(ref capturedPendingValue);
                _output.WriteLine($"Captured pending gauge value: {value}");

                return value > 0;
            }, TimeSpan.FromSeconds(15), message: "Pending gauge should report > 0");

            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            _output.WriteLine($"Pending messages in DB: {pending}, Metric value: {Interlocked.Read(ref capturedPendingValue)}");
            Assert.True(pending > 0, "Messages should be pending in DB");
            Assert.True(Interlocked.Read(ref capturedPendingValue) > 0,
                "outbox.messages.pending gauge should report a value > 0");

            // Restore transport
            transport.SetFailing(false);

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain");

            // Wait for the gauge to reflect the drained state (updated on next heartbeat cycle)
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                meterListener.RecordObservableInstruments();
                var value = Interlocked.Read(ref capturedPendingValue);
                _output.WriteLine($"Pending gauge after drain: {value}");

                return value == 0;
            }, TimeSpan.FromSeconds(15), message: "Pending gauge should reach 0 after drain");

            _output.WriteLine("All messages drained, pending gauge = 0");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            await EventHubTestHelper.DrainHubAsync(_infra.EventHubConnectionString, eventHub, TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }
}
