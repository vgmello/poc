using Xunit;

namespace Outbox.IntegrationTests.Fixtures;

[CollectionDefinition(Name)]
public class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "Infrastructure";
}
