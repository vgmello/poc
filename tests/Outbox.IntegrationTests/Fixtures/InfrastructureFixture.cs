// Copyright (c) OrgName. All rights reserved.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;
using Xunit;

namespace Outbox.IntegrationTests.Fixtures;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private const string SqlServerPassword = "YourStrong!Passw0rd";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private readonly RedpandaContainer _redpanda = new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v24.2.18")
        .Build();

    // Azure SQL Edge supports ARM64 (unlike SQL Server Linux).
    // We use a generic container because the MsSql testcontainer's readiness check
    // requires sqlcmd, which Azure SQL Edge doesn't include.
    private readonly IContainer _sqlServer = new ContainerBuilder("mcr.microsoft.com/azure-sql-edge:latest")
        .WithPortBinding(1433, true)
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithEnvironment("MSSQL_SA_PASSWORD", SqlServerPassword)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .AddCustomWaitStrategy(new SqlServerReadyWaitStrategy()))
        .Build();

    private readonly INetwork _eventHubNetwork = new NetworkBuilder()
        .WithName($"eventhub-network-{Guid.NewGuid():N}")
        .Build();

    private IContainer? _azurite;
    private IContainer? _eventHubsEmulator;

    public string ConnectionString => _postgres.GetConnectionString();
    public string BootstrapServers => _redpanda.GetBootstrapAddress();

    public string SqlServerConnectionString
    {
        get
        {
            var host = _sqlServer.Hostname;
            var port = _sqlServer.GetMappedPublicPort(1433);

            return $"Server={host},{port};Database=master;User Id=sa;Password={SqlServerPassword};TrustServerCertificate=True;";
        }
    }

    public string EventHubConnectionString
    {
        get
        {
            if (_eventHubsEmulator is null)
                throw new InvalidOperationException("EventHubs emulator not initialized");

            var host = _eventHubsEmulator.Hostname;
            var port = _eventHubsEmulator.GetMappedPublicPort(5672);

            return $"Endpoint=sb://{host}:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
        }
    }

    public async Task InitializeAsync()
    {
        var configJson = EmulatorConfigGenerator.Build(
            Outbox.IntegrationTests.Helpers.EventHubTestHelper.PoolSize);

        await _eventHubNetwork.CreateAsync();

        _azurite = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithNetwork(_eventHubNetwork)
            .WithNetworkAliases("azurite")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Azurite Blob service is successfully listening"))
            .Build();

        await Task.WhenAll(
            _postgres.StartAsync(),
            _redpanda.StartAsync(),
            _sqlServer.StartAsync(),
            _azurite.StartAsync());

        _eventHubsEmulator = new ContainerBuilder("mcr.microsoft.com/azure-messaging/eventhubs-emulator:2.2.0")
            .WithNetwork(_eventHubNetwork)
            .WithNetworkAliases("eventhubs-emulator")
            .WithPortBinding(5672, true)
            .WithEnvironment("BLOB_SERVER", "azurite")
            .WithEnvironment("METADATA_SERVER", "azurite")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithResourceMapping(
                System.Text.Encoding.UTF8.GetBytes(configJson),
                "/Eventhubs_Emulator/ConfigFiles/Config.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Emulator Service is Successfully Up"))
            .Build();

        await _eventHubsEmulator.StartAsync();

        await Task.WhenAll(
            RunPostgresSchemaAsync(),
            RunSqlServerSchemaAsync());
    }

    public async Task DisposeAsync()
    {
        var tasks = new List<Task>
        {
            _postgres.DisposeAsync().AsTask(),
            _redpanda.DisposeAsync().AsTask(),
            _sqlServer.DisposeAsync().AsTask(),
        };

        if (_azurite is not null)
            tasks.Add(_azurite.DisposeAsync().AsTask());

        if (_eventHubsEmulator is not null)
            tasks.Add(_eventHubsEmulator.DisposeAsync().AsTask());

        await Task.WhenAll(tasks);
        await _eventHubNetwork.DeleteAsync();
    }

    private async Task RunPostgresSchemaAsync()
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

    private async Task RunSqlServerSchemaAsync()
    {
        var baseDir = AppContext.BaseDirectory;
        var sqlPath = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..",
            "src", "Outbox.SqlServer", "db_scripts", "install.sql"));

        if (!File.Exists(sqlPath))
        {
            sqlPath = Path.GetFullPath(Path.Combine(
                Directory.GetCurrentDirectory(), "..", "..", "..", "..", "..",
                "src", "Outbox.SqlServer", "db_scripts", "install.sql"));
        }

        var sql = await File.ReadAllTextAsync(sqlPath);

        // SQL Server uses GO as a batch separator which SqlCommand cannot handle in a single command.
        // Split on GO (on its own line) and execute each batch separately.
        var batches = sql
            .Split(["\r\nGO", "\nGO", "\r\ngo", "\ngo"], StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b));

        await using var conn = new SqlConnection(SqlServerConnectionString);
        await conn.OpenAsync();

        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    ///     Custom wait strategy that verifies SQL Server is ready by attempting a connection.
    /// </summary>
    private sealed class SqlServerReadyWaitStrategy : IWaitUntil
    {
        public async Task<bool> UntilAsync(IContainer container)
        {
            try
            {
                var host = container.Hostname;
                var port = container.GetMappedPublicPort(1433);
                var connStr =
                    $"Server={host},{port};Database=master;User Id=sa;Password={SqlServerPassword};TrustServerCertificate=True;Connect Timeout=3;";
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
