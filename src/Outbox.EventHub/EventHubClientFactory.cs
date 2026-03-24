// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs.Producer;

namespace Outbox.EventHub;

public delegate EventHubProducerClient EventHubClientFactory(string eventHubName);
