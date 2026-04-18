// Copyright (c) OrgName. All rights reserved.

using System.Text;

namespace Outbox.IntegrationTests.Fixtures;

/// <summary>
///     Builds a Config.json payload for the Microsoft Event Hubs emulator.
///     Declares a namespace "test-ns" with a pool of generic 4-partition hubs
///     plus two named hubs with larger partition counts for ordering/rebalance tests.
/// </summary>
internal static class EmulatorConfigGenerator
{
    public const string NamespaceName = "test-ns";

    public static string Build(int poolSize)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"UserConfig\": {\n");
        sb.Append("    \"NamespaceConfig\": [\n");
        sb.Append("      {\n");
        sb.Append("        \"Type\": \"EventHub\",\n");
        sb.Append($"        \"Name\": \"{NamespaceName}\",\n");
        sb.Append("        \"Entities\": [\n");

        for (var i = 0; i < poolSize; i++)
        {
            sb.Append("          { \"Name\": \"test-hub-");
            sb.Append(i.ToString("D2"));
            sb.Append("\", \"PartitionCount\": \"4\", \"ConsumerGroups\": [] },\n");
        }

        sb.Append("          { \"Name\": \"test-hub-ordering-8p\", \"PartitionCount\": \"8\", \"ConsumerGroups\": [] },\n");
        sb.Append("          { \"Name\": \"test-hub-rebalance-16p\", \"PartitionCount\": \"16\", \"ConsumerGroups\": [] }\n");
        sb.Append("        ]\n");
        sb.Append("      }\n");
        sb.Append("    ],\n");
        sb.Append("    \"LoggingConfig\": { \"Type\": \"File\" }\n");
        sb.Append("  }\n");
        sb.Append("}\n");

        return sb.ToString();
    }
}
