// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.EventHub.Tests;

public class EventHubMessageHelperTests
{
    private static OutboxMessage CreateMessage(
        string eventType = "TestEvent",
        Dictionary<string, string>? headers = null,
        string payload = "{}") =>
        new OutboxMessage(
            SequenceNumber: 1L,
            TopicName: "test-hub",
            PartitionKey: "pk1",
            EventType: eventType,
            Headers: headers,
            Payload: Encoding.UTF8.GetBytes(payload),
            PayloadContentType: "application/json",
            EventDateTimeUtc: DateTimeOffset.UtcNow,
            EventOrdinal: 0,
            CreatedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void CreateEventData_ValidJsonHeaders_PropertiesContainHeaderEntries()
    {
        var headers = new Dictionary<string, string> { { "correlationId", "abc-123" }, { "source", "svc-a" } };
        var msg = CreateMessage(headers: headers);

        var eventData = EventHubMessageHelper.CreateEventData(msg);

        Assert.Equal("abc-123", eventData.Properties["correlationId"]);
        Assert.Equal("svc-a", eventData.Properties["source"]);
    }

    [Fact]
    public void CreateEventData_NullHeaders_EventTypePropertyIsStillSet()
    {
        var msg = CreateMessage(eventType: "OrderCreated", headers: null);

        var eventData = EventHubMessageHelper.CreateEventData(msg);

        Assert.True(eventData.Properties.ContainsKey("EventType"));
        Assert.Equal("OrderCreated", eventData.Properties["EventType"]);
    }

    [Fact]
    public void CreateEventData_EventType_IsSetAsProperty()
    {
        var msg = CreateMessage(eventType: "InventoryUpdated");

        var eventData = EventHubMessageHelper.CreateEventData(msg);

        Assert.Equal("InventoryUpdated", eventData.Properties["EventType"]);
    }

    [Fact]
    public void CreateEventData_EventTypeOverwritesUserSuppliedHeaderValue()
    {
        // Even if headers contain "EventType", the system value wins
        var headers = new Dictionary<string, string> { { "EventType", "user-supplied-value" } };
        var msg = CreateMessage(eventType: "SystemEvent", headers: headers);

        var eventData = EventHubMessageHelper.CreateEventData(msg);

        Assert.Equal("SystemEvent", eventData.Properties["EventType"]);
    }
}
