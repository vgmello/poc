namespace Outbox.PerformanceTests.Helpers;

public enum StoreType
{
    SqlServer,
    PostgreSql
}

public enum TransportType
{
    Redpanda,
    EventHub
}

public sealed record TestCombination(StoreType Store, TransportType Transport, int PublisherCount)
{
    public string Label => $"{Store}+{Transport} {PublisherCount}P";

    public override string ToString() => Label;
}

public static class TestMatrix
{
    private static readonly StoreType[] Stores = [StoreType.SqlServer, StoreType.PostgreSql];
    private static readonly TransportType[] Transports = [TransportType.Redpanda, TransportType.EventHub];
    private static readonly int[] PublisherCounts = [1, 2, 4];

    public static IEnumerable<object[]> AllCombinations()
    {
        foreach (var store in Stores)
        {
            foreach (var transport in Transports)
            {
                foreach (var count in PublisherCounts)
                {
                    yield return [new TestCombination(store, transport, count)];
                }
            }
        }
    }
}
