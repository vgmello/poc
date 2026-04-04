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

    public static IEnumerable<object[]> AllCombinations() => CombinationsFor(Stores, Transports);

    public static IEnumerable<object[]> SqlServerCombinations() => CombinationsFor([StoreType.SqlServer], Transports);

    public static IEnumerable<object[]> PostgreSqlCombinations() => CombinationsFor([StoreType.PostgreSql], Transports);

    public static IEnumerable<object[]> SqlServerRedpandaCombinations() => CombinationsFor([StoreType.SqlServer], [TransportType.Redpanda]);

    public static IEnumerable<object[]> SqlServerEventHubCombinations() => CombinationsFor([StoreType.SqlServer], [TransportType.EventHub]);

    public static IEnumerable<object[]> PostgreSqlRedpandaCombinations() => CombinationsFor([StoreType.PostgreSql], [TransportType.Redpanda]);

    public static IEnumerable<object[]> PostgreSqlEventHubCombinations() => CombinationsFor([StoreType.PostgreSql], [TransportType.EventHub]);

    private static IEnumerable<object[]> CombinationsFor(StoreType[] stores, TransportType[] transports)
    {
        foreach (var store in stores)
        {
            foreach (var transport in transports)
            {
                foreach (var count in PublisherCounts)
                {
                    yield return [new TestCombination(store, transport, count)];
                }
            }
        }
    }
}
