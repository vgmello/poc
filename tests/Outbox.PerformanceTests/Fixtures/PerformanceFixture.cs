using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Data.SqlClient;
using Npgsql;
using Outbox.PerformanceTests.Helpers;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;
using Xunit;

namespace Outbox.PerformanceTests.Fixtures;

public sealed class PerformanceFixture : IAsyncLifetime
{
    private const string SqlServerPassword = "YourStrong!Passw0rd";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private readonly RedpandaContainer _redpanda = new RedpandaBuilder(
            "docker.redpanda.com/redpandadata/redpanda:v24.2.18")
        .Build();

    private INetwork _eventHubNetwork = null!;
    private IContainer _azurite = null!;
    private IContainer _eventHubEmulator = null!;

    // Azure SQL Edge — same approach as integration tests
    private IContainer _sqlServer = null!;

    public string PostgreSqlConnectionString => _postgres.GetConnectionString();
    public string SqlServerConnectionString { get; private set; } = "";
    public string BootstrapServers => _redpanda.GetBootstrapAddress();
    public string EventHubConnectionString { get; private set; } = "";

    // Results collected during test runs
    private readonly List<BulkResult> _bulkResults = [];
    private readonly List<SustainedResult> _sustainedResults = [];
    private readonly object _resultsLock = new();

    public void AddBulkResult(BulkResult result)
    {
        lock (_resultsLock) _bulkResults.Add(result);
    }

    public void AddSustainedResult(SustainedResult result)
    {
        lock (_resultsLock) _sustainedResults.Add(result);
    }

    public async Task InitializeAsync()
    {
        // Start independent containers in parallel
        _eventHubNetwork = new NetworkBuilder().Build();
        await _eventHubNetwork.CreateAsync();

        _azurite = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithNetwork(_eventHubNetwork)
            .WithNetworkAliases("azurite")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Azurite Blob service is successfully listening"))
            .Build();

        _sqlServer = new ContainerBuilder("mcr.microsoft.com/azure-sql-edge:latest")
            .WithPortBinding(1433, true)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", SqlServerPassword)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new SqlServerReadyWaitStrategy()))
            .Build();

        // Start postgres, redpanda, sql server, azurite in parallel
        await Task.WhenAll(
            _postgres.StartAsync(),
            _redpanda.StartAsync(),
            _sqlServer.StartAsync(),
            _azurite.StartAsync());

        // Build SQL Server connection string
        var sqlPort = _sqlServer.GetMappedPublicPort(1433);
        var sqlHost = _sqlServer.Hostname;
        SqlServerConnectionString = $"Server={sqlHost},{sqlPort};Database=master;User Id=sa;Password={SqlServerPassword};TrustServerCertificate=True;";

        // Start EventHub emulator (depends on Azurite being ready)
        var configJson = BuildEventHubConfig();
        _eventHubEmulator = new ContainerBuilder("mcr.microsoft.com/azure-messaging/eventhubs-emulator:latest")
            .WithNetwork(_eventHubNetwork)
            .WithNetworkAliases("eventhubs-emulator")
            .WithPortBinding(5672, true)
            .WithEnvironment("BLOB_SERVER", "azurite")
            .WithEnvironment("METADATA_SERVER", "azurite")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(configJson),
                FilePath.Of("/Eventhubs_Emulator/ConfigFiles/Config.json"))
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Emulator Service is Successfully Up"))
            .Build();

        await _eventHubEmulator.StartAsync();

        var ehPort = _eventHubEmulator.GetMappedPublicPort(5672);
        var ehHost = _eventHubEmulator.Hostname;
        EventHubConnectionString = $"Endpoint=sb://{ehHost}:{ehPort};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

        // Give the EventHub emulator extra time to fully initialize AMQP listeners
        await Task.Delay(TimeSpan.FromSeconds(15));

        // Initialize schemas
        await Task.WhenAll(
            RunPostgresSchemaAsync(),
            RunSqlServerSchemaAsync());
    }

    public async Task DisposeAsync()
    {
        // Write report before tearing down containers
        List<BulkResult> bulkCopy;
        List<SustainedResult> sustainedCopy;
        lock (_resultsLock)
        {
            bulkCopy = [.. _bulkResults];
            sustainedCopy = [.. _sustainedResults];
        }

        if (bulkCopy.Count > 0 || sustainedCopy.Count > 0)
        {
            var reportsDir = FindReportsDir();
            await PerfReportWriter.WriteMarkdownReportAsync(bulkCopy, sustainedCopy, reportsDir);
        }

        var disposeTasks = new List<Task>();
        if (_postgres is not null) disposeTasks.Add(_postgres.DisposeAsync().AsTask());
        if (_redpanda is not null) disposeTasks.Add(_redpanda.DisposeAsync().AsTask());
        if (_sqlServer is not null) disposeTasks.Add(_sqlServer.DisposeAsync().AsTask());
        if (_eventHubEmulator is not null) disposeTasks.Add(_eventHubEmulator.DisposeAsync().AsTask());
        if (_azurite is not null) disposeTasks.Add(_azurite.DisposeAsync().AsTask());
        await Task.WhenAll(disposeTasks);

        if (_eventHubNetwork is not null) await _eventHubNetwork.DisposeAsync();
    }

    private static string FindReportsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests/Outbox.PerformanceTests")))
            dir = dir.Parent;

        return dir is not null
            ? Path.Combine(dir.FullName, "tests/Outbox.PerformanceTests/reports")
            : Path.Combine(AppContext.BaseDirectory, "reports");
    }

    private static string BuildEventHubConfig()
    {
        // Create entities for the 3 perf test topics
        return """
        {
            "UserConfig": {
                "NamespaceConfig": [
                    {
                        "Type": "EventHub",
                        "Name": "emulatorNs1",
                        "Entities": [
                            { "Name": "perf-topic-0", "PartitionCount": "8", "ConsumerGroups": [] },
                            { "Name": "perf-topic-1", "PartitionCount": "8", "ConsumerGroups": [] },
                            { "Name": "perf-topic-2", "PartitionCount": "8", "ConsumerGroups": [] }
                        ]
                    }
                ],
                "LoggingConfig": { "Type": "File" }
            }
        }
        """;
    }

    private async Task RunPostgresSchemaAsync()
    {
        var schemaPath = FindSchemaFile("src/Outbox.PostgreSQL/db_scripts/install.sql");
        var sql = await File.ReadAllTextAsync(schemaPath);

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RunSqlServerSchemaAsync()
    {
        var schemaPath = FindSchemaFile("src/Outbox.SqlServer/db_scripts/install.sql");
        var sql = await File.ReadAllTextAsync(schemaPath);

        await using var conn = new SqlConnection(SqlServerConnectionString);
        await conn.OpenAsync();

        // SQL Server uses GO as a batch separator which SqlCommand cannot handle in a single command.
        // Split on GO (on its own line) and execute each batch separately.
        var batches = sql
            .Split(["\r\nGO", "\nGO", "\r\ngo", "\ngo"], StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b));

        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string FindSchemaFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relativePath)))
            dir = dir.Parent;

        return dir is not null
            ? Path.Combine(dir.FullName, relativePath)
            : throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}");
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
