// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs;
using Outbox.Core.Models;

namespace Outbox.EventHub;

internal static class EventHubMessageHelper
{
    internal static EventData CreateEventData(OutboxMessage msg)
    {
        var eventData = new EventData(msg.Payload);

        if (msg.Headers is not null)
        {
            foreach (var kvp in msg.Headers)
                eventData.Properties[kvp.Key] = kvp.Value;
        }

        eventData.Properties["EventType"] = msg.EventType;

        return eventData;
    }
}
