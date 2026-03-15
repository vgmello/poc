using System.Text.Json;

namespace Outbox.Samples.Shared;

public sealed record SampleEvent(string OrderId, string Customer, decimal Amount)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static SampleEvent Generate(string partitionKey)
    {
        var random = Random.Shared;
        return new SampleEvent(
            OrderId: Guid.NewGuid().ToString("N")[..8],
            Customer: partitionKey,
            Amount: Math.Round((decimal)(random.NextDouble() * 500 + 10), 2));
    }
}
