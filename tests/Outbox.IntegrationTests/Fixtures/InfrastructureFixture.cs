using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;
using Xunit;

namespace Outbox.IntegrationTests.Fixtures;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private readonly RedpandaContainer _redpanda = new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v24.2.18")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();
    public string BootstrapServers => _redpanda.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _postgres.StartAsync(),
            _redpanda.StartAsync());

        await RunSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redpanda.DisposeAsync().AsTask());
    }

    private async Task RunSchemaAsync()
    {
        // Find install.sql relative to the test assembly
        var baseDir = AppContext.BaseDirectory;
        var sqlPath = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..",
            "src", "Outbox.PostgreSQL", "db_scripts", "install.sql"));

        if (!File.Exists(sqlPath))
        {
            // Fallback: search from working directory
            sqlPath = Path.GetFullPath(Path.Combine(
                Directory.GetCurrentDirectory(), "..", "..", "..", "..", "..",
                "src", "Outbox.PostgreSQL", "db_scripts", "install.sql"));
        }

        var sql = await File.ReadAllTextAsync(sqlPath);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
