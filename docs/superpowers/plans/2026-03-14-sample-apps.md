# Sample Apps Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build 4 sample console apps validating all (DB x Transport) combos with Docker Compose infrastructure.

**Architecture:** One `samples/` directory with a shared docker-compose bringing up Azure SQL Edge, PostgreSQL, Redpanda, EventHub Emulator, and Azurite. A DbInit service initializes both databases and creates the Redpanda topic. Four .NET 10 worker service apps each wire up the library with a specific DB+transport combo, insert outbox rows on a timer, and consume messages from the broker.

**Tech Stack:** .NET 10, Docker Compose, Azure SQL Edge, PostgreSQL 17, Redpanda, Azure EventHub Emulator, Azurite

**Spec:** `docs/superpowers/specs/2026-03-14-sample-apps-design.md`

---

## File Structure

```
samples/
├── docker-compose.yml
├── .env
├── eventhub-config/
│   └── Config.json
├── DbInit/
│   ├── Dockerfile
│   ├── DbInit.csproj
│   ├── Program.cs
│   └── init-redpanda.sh
├── Shared/
│   ├── Shared.csproj
│   ├── SampleEvent.cs
│   ├── EventProducerBase.cs
│   ├── KafkaConsumer.cs
│   └── EventHubConsumer.cs
├── Sample.SqlServer.Kafka/
│   ├── Dockerfile
│   ├── Sample.SqlServer.Kafka.csproj
│   ├── Program.cs
│   ├── SqlServerEventProducer.cs
│   └── appsettings.json
├── Sample.SqlServer.EventHub/
│   ├── Dockerfile
│   ├── Sample.SqlServer.EventHub.csproj
│   ├── Program.cs
│   ├── SqlServerEventProducer.cs
│   └── appsettings.json
├── Sample.PostgreSQL.Kafka/
│   ├── Dockerfile
│   ├── Sample.PostgreSQL.Kafka.csproj
│   ├── Program.cs
│   ├── PostgreSqlEventProducer.cs
│   └── appsettings.json
└── Sample.PostgreSQL.EventHub/
    ├── Dockerfile
    ├── Sample.PostgreSQL.EventHub.csproj
    ├── Program.cs
    ├── PostgreSqlEventProducer.cs
    └── appsettings.json
```

**Note on Dockerfiles:** Each sample app's Dockerfile needs the repo root as build context because it references library projects under `src/`. The docker-compose sets `context: ..` (repo root) and `dockerfile: samples/<app>/Dockerfile`.

---

## Chunk 1: Infrastructure & Shared Code

### Task 1: EventHub Emulator Config

**Files:**
- Create: `samples/eventhub-config/Config.json`

- [ ] **Step 1: Create EventHub emulator config**

Create `samples/eventhub-config/Config.json`:

```json
{
    "UserConfig": {
        "NamespaceConfig": [
            {
                "Type": "EventHub",
                "Name": "emulatorNs1",
                "Entities": [
                    {
                        "Name": "orders",
                        "PartitionCount": "8",
                        "ConsumerGroups": [
                            {
                                "Name": "$Default"
                            }
                        ]
                    }
                ]
            }
        ],
        "LoggingConfig": {
            "Type": "File"
        }
    }
}
```

- [ ] **Step 2: Create .env file**

Create `samples/.env`:

```
SA_PASSWORD=OutboxP@ss123!
POSTGRES_PASSWORD=OutboxP@ss123!
ACCEPT_EULA=Y
```

- [ ] **Step 3: Commit**

```bash
git add samples/eventhub-config/ samples/.env
git commit -m "feat(samples): add EventHub emulator config and env vars"
```

---

### Task 2: Docker Compose

**Files:**
- Create: `samples/docker-compose.yml`

- [ ] **Step 1: Create docker-compose.yml**

Create `samples/docker-compose.yml`:

```yaml
services:
  azuresqledge:
    image: mcr.microsoft.com/azure-sql-edge:latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "${SA_PASSWORD}"
    ports:
      - "1433:1433"
    healthcheck:
      test: ["CMD-SHELL", "python3 -c \"import socket; s=socket.create_connection(('localhost',1433), timeout=2); s.close()\""]
      interval: 5s
      timeout: 5s
      start_period: 15s
      retries: 20
    networks:
      - outbox-net

  postgres:
    image: postgres:17
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: "${POSTGRES_PASSWORD}"
      POSTGRES_DB: OutboxSample
    ports:
      - "5432:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 5s
      timeout: 5s
      retries: 10
    networks:
      - outbox-net

  redpanda:
    image: redpandadata/redpanda:v24.2.7
    command:
      - redpanda
      - start
      - --smp=1
      - --memory=512M
      - --overprovisioned
      - --kafka-addr=internal://0.0.0.0:9092,external://0.0.0.0:19092
      - --advertise-kafka-addr=internal://redpanda:9092,external://localhost:19092
    ports:
      - "19092:19092"
      - "9644:9644"
    healthcheck:
      test: curl -f http://localhost:9644/v1/status/ready
      interval: 5s
      timeout: 5s
      retries: 10
    networks:
      - outbox-net

  azurite:
    image: mcr.microsoft.com/azure-storage/azurite:latest
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"
    networks:
      - outbox-net

  eventhub-emulator:
    image: mcr.microsoft.com/azure-messaging/eventhubs-emulator:latest
    volumes:
      - ./eventhub-config/Config.json:/Eventhubs_Emulator/ConfigFiles/Config.json
    ports:
      - "5672:5672"
    environment:
      BLOB_SERVER: azurite
      METADATA_SERVER: azurite
      ACCEPT_EULA: "${ACCEPT_EULA}"
    depends_on:
      azurite:
        condition: service_started
    networks:
      outbox-net:
        aliases:
          - "eventhubs-emulator"

  dbinit:
    build:
      context: ..
      dockerfile: samples/DbInit/Dockerfile
    environment:
      SQLSERVER_CONN: "Server=azuresqledge;Database=OutboxSample;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=true"
      POSTGRES_CONN: "Host=postgres;Database=OutboxSample;Username=postgres;Password=${POSTGRES_PASSWORD}"
      REDPANDA_BROKERS: "redpanda:9092"
    depends_on:
      azuresqledge:
        condition: service_healthy
      postgres:
        condition: service_healthy
      redpanda:
        condition: service_healthy
    networks:
      - outbox-net

  # --- Sample Apps ---

  sample-sqlserver-kafka:
    build:
      context: ..
      dockerfile: samples/Sample.SqlServer.Kafka/Dockerfile
    environment:
      ConnectionStrings__OutboxDb: "Server=azuresqledge;Database=OutboxSample;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=true"
      Outbox__Kafka__BootstrapServers: "redpanda:9092"
    depends_on:
      dbinit:
        condition: service_completed_successfully
      redpanda:
        condition: service_healthy
    restart: on-failure
    networks:
      - outbox-net

  sample-sqlserver-eventhub:
    build:
      context: ..
      dockerfile: samples/Sample.SqlServer.EventHub/Dockerfile
    environment:
      ConnectionStrings__OutboxDb: "Server=azuresqledge;Database=OutboxSample;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=true"
      Outbox__EventHub__ConnectionString: "Endpoint=sb://eventhubs-emulator;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
      Outbox__EventHub__EventHubName: "orders"
    depends_on:
      dbinit:
        condition: service_completed_successfully
      eventhub-emulator:
        condition: service_started
    restart: on-failure
    networks:
      - outbox-net

  sample-postgresql-kafka:
    build:
      context: ..
      dockerfile: samples/Sample.PostgreSQL.Kafka/Dockerfile
    environment:
      ConnectionStrings__OutboxDb: "Host=postgres;Database=OutboxSample;Username=postgres;Password=${POSTGRES_PASSWORD}"
      Outbox__Kafka__BootstrapServers: "redpanda:9092"
    depends_on:
      dbinit:
        condition: service_completed_successfully
      redpanda:
        condition: service_healthy
    restart: on-failure
    networks:
      - outbox-net

  sample-postgresql-eventhub:
    build:
      context: ..
      dockerfile: samples/Sample.PostgreSQL.EventHub/Dockerfile
    environment:
      ConnectionStrings__OutboxDb: "Host=postgres;Database=OutboxSample;Username=postgres;Password=${POSTGRES_PASSWORD}"
      Outbox__EventHub__ConnectionString: "Endpoint=sb://eventhubs-emulator;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
      Outbox__EventHub__EventHubName: "orders"
    depends_on:
      dbinit:
        condition: service_completed_successfully
      eventhub-emulator:
        condition: service_started
    restart: on-failure
    networks:
      - outbox-net

networks:
  outbox-net:
    driver: bridge
```

- [ ] **Step 2: Commit**

```bash
git add samples/docker-compose.yml
git commit -m "feat(samples): add docker-compose with all infrastructure and sample services"
```

---

### Task 3: DbInit Service

**Files:**
- Create: `samples/DbInit/DbInit.csproj`
- Create: `samples/DbInit/Program.cs`
- Create: `samples/DbInit/Dockerfile`

The DbInit service:
1. Connects to Azure SQL Edge, creates the database, runs the SQL Server install.sql
2. Connects to PostgreSQL, runs the PostgreSQL install.sql
3. Creates the `orders` topic in Redpanda via HTTP (Admin API)
4. Overrides partition count from 32 → 8 in both databases

- [ ] **Step 1: Create DbInit.csproj**

Create `samples/DbInit/DbInit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.0.1" />
    <PackageReference Include="Npgsql" Version="9.0.3" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create Program.cs**

Create `samples/DbInit/Program.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;

var sqlServerConn = Environment.GetEnvironmentVariable("SQLSERVER_CONN")
    ?? throw new InvalidOperationException("SQLSERVER_CONN not set");
var postgresConn = Environment.GetEnvironmentVariable("POSTGRES_CONN")
    ?? throw new InvalidOperationException("POSTGRES_CONN not set");
var redpandaBrokers = Environment.GetEnvironmentVariable("REDPANDA_BROKERS")
    ?? "redpanda:9092";

// --- SQL Server ---
Console.WriteLine("[DbInit] Initializing SQL Server...");
var masterConnStr = new SqlConnectionStringBuilder(sqlServerConn)
{
    InitialCatalog = "master"
}.ConnectionString;

// Wait and connect to master
await using var masterConn = await RetryOpenSqlAsync(masterConnStr);

// Create database if not exists
await using (var cmd = new SqlCommand(
    "IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'OutboxSample') CREATE DATABASE OutboxSample;",
    masterConn))
{
    cmd.CommandTimeout = 30;
    await cmd.ExecuteNonQueryAsync();
}
await masterConn.CloseAsync();

// Run install.sql from the library's db_scripts
var sqlServerInstall = await File.ReadAllTextAsync("/db_scripts/sqlserver/install.sql");

// Split on GO batch separators
var batches = Regex.Split(sqlServerInstall, @"^\s*GO\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)
    .Where(b => !string.IsNullOrWhiteSpace(b))
    .ToList();

await using var sqlConn = await RetryOpenSqlAsync(sqlServerConn);
foreach (var batch in batches)
{
    await using var cmd = new SqlCommand(batch, sqlConn);
    cmd.CommandTimeout = 30;
    await cmd.ExecuteNonQueryAsync();
}

// Override partitions: delete default 32, insert 8
await using (var cmd = new SqlCommand("""
    DELETE FROM dbo.OutboxPartitions;
    ;WITH Numbers AS (
        SELECT 0 AS N UNION ALL SELECT N + 1 FROM Numbers WHERE N < 7
    )
    INSERT INTO dbo.OutboxPartitions (PartitionId)
    SELECT N FROM Numbers
    WHERE NOT EXISTS (SELECT 1 FROM dbo.OutboxPartitions WHERE PartitionId = N);
    """, sqlConn))
{
    await cmd.ExecuteNonQueryAsync();
}
Console.WriteLine("[DbInit] SQL Server initialized with 8 partitions.");

// --- PostgreSQL ---
Console.WriteLine("[DbInit] Initializing PostgreSQL...");
var pgInstall = await File.ReadAllTextAsync("/db_scripts/pgsql/install.sql");

await using var pgConn = await RetryOpenPgAsync(postgresConn);
await using (var cmd = new NpgsqlCommand(pgInstall, pgConn))
{
    cmd.CommandTimeout = 30;
    await cmd.ExecuteNonQueryAsync();
}

// Override partitions: delete default 32, insert 8
await using (var cmd = new NpgsqlCommand("""
    DELETE FROM outbox_partitions;
    INSERT INTO outbox_partitions (partition_id)
    SELECT generate_series(0, 7)
    ON CONFLICT DO NOTHING;
    """, pgConn))
{
    await cmd.ExecuteNonQueryAsync();
}
Console.WriteLine("[DbInit] PostgreSQL initialized with 8 partitions.");

// --- Redpanda Topic ---
Console.WriteLine("[DbInit] Creating Redpanda topic...");
var redpandaHost = redpandaBrokers.Replace(":9092", "");
using var http = new HttpClient { BaseAddress = new Uri($"http://{redpandaHost}:9644") };

try
{
    // Create topic via Admin API
    var response = await http.PostAsJsonAsync("/v1/topics", new
    {
        topic = "orders",
        partition_count = 8,
        replication_factor = 1
    });

    if (response.IsSuccessStatusCode)
        Console.WriteLine("[DbInit] Topic 'orders' created with 8 partitions.");
    else
    {
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[DbInit] Topic creation response: {response.StatusCode} - {body}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[DbInit] Topic creation warning: {ex.Message}");
}

Console.WriteLine("[DbInit] All initialization complete.");

// --- Helpers ---

static async Task<SqlConnection> RetryOpenSqlAsync(string connStr, int maxRetries = 30)
{
    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            return conn;
        }
        catch
        {
            Console.WriteLine($"[DbInit] SQL Server not ready, retrying ({i + 1}/{maxRetries})...");
            await Task.Delay(2000);
        }
    }
    throw new InvalidOperationException("SQL Server did not become ready");
}

static async Task<NpgsqlConnection> RetryOpenPgAsync(string connStr, int maxRetries = 30)
{
    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            return conn;
        }
        catch
        {
            Console.WriteLine($"[DbInit] PostgreSQL not ready, retrying ({i + 1}/{maxRetries})...");
            await Task.Delay(2000);
        }
    }
    throw new InvalidOperationException("PostgreSQL did not become ready");
}
```

- [ ] **Step 3: Create Dockerfile**

Create `samples/DbInit/Dockerfile`. Build context is repo root (`..` from samples/).

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY samples/DbInit/DbInit.csproj samples/DbInit/
RUN dotnet restore samples/DbInit/DbInit.csproj
COPY samples/DbInit/ samples/DbInit/
RUN dotnet publish samples/DbInit/DbInit.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# Copy install scripts from the library packages
COPY src/Outbox.SqlServer/db_scripts/install.sql /db_scripts/sqlserver/install.sql
COPY src/Outbox.PostgreSQL/db_scripts/install.sql /db_scripts/pgsql/install.sql

COPY --from=build /app .
ENTRYPOINT ["dotnet", "DbInit.dll"]
```

- [ ] **Step 4: Build to verify**

```bash
cd /home/vgmello/repos/poc/poc
dotnet build samples/DbInit/DbInit.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add samples/DbInit/
git commit -m "feat(samples): add DbInit service for SQL Server, PostgreSQL, and Redpanda"
```

---

### Task 4: Shared Code

**Files:**
- Create: `samples/Shared/Shared.csproj`
- Create: `samples/Shared/SampleEvent.cs`
- Create: `samples/Shared/EventProducerBase.cs`
- Create: `samples/Shared/KafkaConsumer.cs`
- Create: `samples/Shared/EventHubConsumer.cs`

- [ ] **Step 1: Create Shared.csproj**

Create `samples/Shared/Shared.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Outbox.Samples.Shared</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageReference Include="Confluent.Kafka" Version="2.8.0" />
    <PackageReference Include="Azure.Messaging.EventHubs" Version="5.12.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create SampleEvent.cs**

Create `samples/Shared/SampleEvent.cs`:

```csharp
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
```

- [ ] **Step 3: Create EventProducerBase.cs**

Create `samples/Shared/EventProducerBase.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Outbox.Samples.Shared;

public abstract class EventProducerBase : BackgroundService
{
    private readonly ILogger _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
    private readonly int _partitionKeyCount = 8;

    protected EventProducerBase(ILogger logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the publisher a moment to start
        await Task.Delay(3000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batchSize = Random.Shared.Next(3, 6);
                var partitionKey = $"customer-{Random.Shared.Next(1, _partitionKeyCount + 1)}";

                for (int i = 0; i < batchSize; i++)
                {
                    var evt = SampleEvent.Generate(partitionKey);
                    var headers = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["source"] = "sample-app",
                        ["correlationId"] = Guid.NewGuid().ToString("N")[..12]
                    });

                    await InsertOutboxRowAsync(
                        topicName: "orders",
                        partitionKey: partitionKey,
                        eventType: "OrderCreated",
                        headers: headers,
                        payload: evt.ToJson(),
                        stoppingToken);
                }

                _logger.LogInformation("EventProducer: Inserted {Count} events for {PartitionKey}",
                    batchSize, partitionKey);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EventProducer: Failed to insert events");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    protected abstract Task InsertOutboxRowAsync(
        string topicName,
        string partitionKey,
        string eventType,
        string headers,
        string payload,
        CancellationToken ct);
}
```

- [ ] **Step 4: Create KafkaConsumer.cs**

Create `samples/Shared/KafkaConsumer.cs`:

```csharp
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Outbox.Samples.Shared;

public sealed class KafkaConsumer : BackgroundService
{
    private readonly ILogger<KafkaConsumer> _logger;
    private readonly string _bootstrapServers;
    private readonly string _topic;

    public KafkaConsumer(ILogger<KafkaConsumer> logger, IConfiguration configuration)
    {
        _logger = logger;
        _bootstrapServers = configuration["Outbox:Kafka:BootstrapServers"] ?? "localhost:9092";
        _topic = "orders";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(5000, stoppingToken); // Wait for broker

        var config = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = $"sample-consumer-{Guid.NewGuid():N}"[..20],
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_topic);

        _logger.LogInformation("KafkaConsumer: Subscribed to {Topic}", _topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result is null) continue;

                _logger.LogInformation(
                    "Consumer: Received event from {Topic}/{PartitionKey} — {Payload}",
                    result.Topic, result.Message.Key, result.Message.Value);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "KafkaConsumer: Consume error");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        consumer.Close();
    }
}
```

- [ ] **Step 5: Create EventHubConsumer.cs**

Create `samples/Shared/EventHubConsumer.cs`:

```csharp
using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Outbox.Samples.Shared;

public sealed class EventHubConsumer : BackgroundService
{
    private readonly ILogger<EventHubConsumer> _logger;
    private readonly string _connectionString;
    private readonly string _eventHubName;

    public EventHubConsumer(ILogger<EventHubConsumer> logger, IConfiguration configuration)
    {
        _logger = logger;
        _connectionString = configuration["Outbox:EventHub:ConnectionString"]
            ?? throw new InvalidOperationException("EventHub ConnectionString not configured");
        _eventHubName = configuration["Outbox:EventHub:EventHubName"] ?? "orders";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(10000, stoppingToken); // Wait for EventHub emulator

        await using var consumer = new EventHubConsumerClient(
            EventHubConsumerClient.DefaultConsumerGroupName,
            _connectionString,
            _eventHubName);

        _logger.LogInformation("EventHubConsumer: Listening on {EventHub}", _eventHubName);

        await foreach (var partitionEvent in consumer.ReadEventsAsync(stoppingToken))
        {
            var body = partitionEvent.Data.EventBody.ToString();
            var partitionKey = partitionEvent.Data.PartitionKey ?? "unknown";

            _logger.LogInformation(
                "Consumer: Received event from {EventHub}/{PartitionKey} — {Payload}",
                _eventHubName, partitionKey, body);
        }
    }
}
```

- [ ] **Step 6: Build to verify**

```bash
dotnet build samples/Shared/Shared.csproj
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add samples/Shared/
git commit -m "feat(samples): add shared code (EventProducerBase, consumers, SampleEvent)"
```

---

## Chunk 2: Sample Apps

### Task 5: Sample.SqlServer.Kafka

**Files:**
- Create: `samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj`
- Create: `samples/Sample.SqlServer.Kafka/Program.cs`
- Create: `samples/Sample.SqlServer.Kafka/SqlServerEventProducer.cs`
- Create: `samples/Sample.SqlServer.Kafka/appsettings.json`
- Create: `samples/Sample.SqlServer.Kafka/Dockerfile`

- [ ] **Step 1: Create csproj**

Create `samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Outbox.Core/Outbox.Core.csproj" />
    <ProjectReference Include="../../src/Outbox.SqlServer/Outbox.SqlServer.csproj" />
    <ProjectReference Include="../../src/Outbox.Kafka/Outbox.Kafka.csproj" />
    <ProjectReference Include="../Shared/Shared.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create Program.cs**

Create `samples/Sample.SqlServer.Kafka/Program.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Outbox.Core.Builder;
using Outbox.Kafka;
using Outbox.Samples.Shared;
using Outbox.SqlServer;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UseSqlServer(async (sp, ct) =>
    {
        var connStr = builder.Configuration.GetConnectionString("OutboxDb")
            ?? throw new InvalidOperationException("ConnectionStrings:OutboxDb not set");
        var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        return conn;
    });

    outbox.UseKafka();
});

builder.Services.AddHostedService<SqlServerEventProducer>();
builder.Services.AddHostedService<KafkaConsumer>();

var host = builder.Build();
await host.RunAsync();
```

- [ ] **Step 3: Create SqlServerEventProducer.cs**

Create `samples/Sample.SqlServer.Kafka/SqlServerEventProducer.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Outbox.Samples.Shared;

namespace Sample.SqlServer.Kafka;

public sealed class SqlServerEventProducer : EventProducerBase
{
    private readonly string _connectionString;

    public SqlServerEventProducer(
        ILogger<SqlServerEventProducer> logger,
        IConfiguration configuration) : base(logger)
    {
        _connectionString = configuration.GetConnectionString("OutboxDb")
            ?? throw new InvalidOperationException("ConnectionStrings:OutboxDb not set");
    }

    protected override async Task InsertOutboxRowAsync(
        string topicName, string partitionKey, string eventType,
        string headers, string payload, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("""
            INSERT INTO dbo.Outbox
                (TopicName, PartitionKey, EventType, Headers, Payload, EventDateTimeUtc, EventOrdinal)
            VALUES
                (@topicName, @partitionKey, @eventType, @headers, @payload, SYSUTCDATETIME(), 0)
            """, conn);

        cmd.Parameters.AddWithValue("@topicName", topicName);
        cmd.Parameters.AddWithValue("@partitionKey", partitionKey);
        cmd.Parameters.AddWithValue("@eventType", eventType);
        cmd.Parameters.AddWithValue("@headers", headers);
        cmd.Parameters.AddWithValue("@payload", payload);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 4: Create appsettings.json**

Create `samples/Sample.SqlServer.Kafka/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting": "Warning"
    }
  },
  "ConnectionStrings": {
    "OutboxDb": "Server=azuresqledge;Database=OutboxSample;User Id=sa;Password=OutboxP@ss123!;TrustServerCertificate=true"
  },
  "Outbox": {
    "Publisher": {
      "BatchSize": 10,
      "MinPollIntervalMs": 200,
      "MaxPollIntervalMs": 2000
    },
    "Kafka": {
      "BootstrapServers": "redpanda:9092"
    }
  }
}
```

- [ ] **Step 5: Create Dockerfile**

Create `samples/Sample.SqlServer.Kafka/Dockerfile` (build context is repo root):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Outbox.Core/Outbox.Core.csproj src/Outbox.Core/
COPY src/Outbox.SqlServer/Outbox.SqlServer.csproj src/Outbox.SqlServer/
COPY src/Outbox.Kafka/Outbox.Kafka.csproj src/Outbox.Kafka/
COPY samples/Shared/Shared.csproj samples/Shared/
COPY samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj samples/Sample.SqlServer.Kafka/
RUN dotnet restore samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj
COPY src/ src/
COPY samples/Shared/ samples/Shared/
COPY samples/Sample.SqlServer.Kafka/ samples/Sample.SqlServer.Kafka/
RUN dotnet publish samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sample.SqlServer.Kafka.dll"]
```

- [ ] **Step 6: Build and commit**

```bash
dotnet build samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj
git add samples/Sample.SqlServer.Kafka/
git commit -m "feat(samples): add Sample.SqlServer.Kafka app"
```

---

### Task 6: Sample.SqlServer.EventHub

**Files:**
- Create: `samples/Sample.SqlServer.EventHub/Sample.SqlServer.EventHub.csproj`
- Create: `samples/Sample.SqlServer.EventHub/Program.cs`
- Create: `samples/Sample.SqlServer.EventHub/SqlServerEventProducer.cs`
- Create: `samples/Sample.SqlServer.EventHub/appsettings.json`
- Create: `samples/Sample.SqlServer.EventHub/Dockerfile`

Same pattern as Task 5 but wires `outbox.UseEventHub()` instead of `outbox.UseKafka()`, registers `EventHubConsumer` instead of `KafkaConsumer`, and references `Outbox.EventHub` instead of `Outbox.Kafka`.

The `SqlServerEventProducer.cs` is identical to Task 5's version (same DB, different transport).

- [ ] **Step 1: Create all files following Task 5's pattern**

Key differences in `Program.cs`:
```csharp
using Outbox.EventHub;
// ...
outbox.UseEventHub();
// ...
builder.Services.AddHostedService<EventHubConsumer>();
```

Key differences in `appsettings.json`:
```json
{
  "Outbox": {
    "Publisher": { "BatchSize": 10, "MinPollIntervalMs": 200, "MaxPollIntervalMs": 2000 },
    "EventHub": {
      "ConnectionString": "Endpoint=sb://eventhubs-emulator;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
      "EventHubName": "orders"
    }
  }
}
```

Dockerfile references `Outbox.EventHub` instead of `Outbox.Kafka`.

- [ ] **Step 2: Build and commit**

```bash
dotnet build samples/Sample.SqlServer.EventHub/Sample.SqlServer.EventHub.csproj
git add samples/Sample.SqlServer.EventHub/
git commit -m "feat(samples): add Sample.SqlServer.EventHub app"
```

---

### Task 7: Sample.PostgreSQL.Kafka

**Files:**
- Create: `samples/Sample.PostgreSQL.Kafka/Sample.PostgreSQL.Kafka.csproj`
- Create: `samples/Sample.PostgreSQL.Kafka/Program.cs`
- Create: `samples/Sample.PostgreSQL.Kafka/PostgreSqlEventProducer.cs`
- Create: `samples/Sample.PostgreSQL.Kafka/appsettings.json`
- Create: `samples/Sample.PostgreSQL.Kafka/Dockerfile`

Same pattern as Task 5 but wires `outbox.UsePostgreSql()` instead of `outbox.UseSqlServer()`, uses `NpgsqlConnection` for inserts, references `Outbox.PostgreSQL`.

- [ ] **Step 1: Create all files**

Key differences in `Program.cs`:
```csharp
using Npgsql;
using Outbox.PostgreSQL;
// ...
outbox.UsePostgreSql(async (sp, ct) =>
{
    var connStr = builder.Configuration.GetConnectionString("OutboxDb")!;
    var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync(ct);
    return conn;
});
```

`PostgreSqlEventProducer.cs`:
```csharp
protected override async Task InsertOutboxRowAsync(
    string topicName, string partitionKey, string eventType,
    string headers, string payload, CancellationToken ct)
{
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync(ct);

    await using var cmd = new NpgsqlCommand("""
        INSERT INTO outbox
            (topic_name, partition_key, event_type, headers, payload, event_datetime_utc, event_ordinal)
        VALUES
            (@topicName, @partitionKey, @eventType, @headers, @payload, NOW(), 0)
        """, conn);

    cmd.Parameters.AddWithValue("@topicName", topicName);
    cmd.Parameters.AddWithValue("@partitionKey", partitionKey);
    cmd.Parameters.AddWithValue("@eventType", eventType);
    cmd.Parameters.AddWithValue("@headers", headers);
    cmd.Parameters.AddWithValue("@payload", payload);

    await cmd.ExecuteNonQueryAsync(ct);
}
```

`appsettings.json`:
```json
{
  "ConnectionStrings": {
    "OutboxDb": "Host=postgres;Database=OutboxSample;Username=postgres;Password=OutboxP@ss123!"
  },
  "Outbox": {
    "Publisher": { "BatchSize": 10, "MinPollIntervalMs": 200, "MaxPollIntervalMs": 2000 },
    "Kafka": { "BootstrapServers": "redpanda:9092" }
  }
}
```

Dockerfile references `Outbox.PostgreSQL` instead of `Outbox.SqlServer`.

- [ ] **Step 2: Build and commit**

```bash
dotnet build samples/Sample.PostgreSQL.Kafka/Sample.PostgreSQL.Kafka.csproj
git add samples/Sample.PostgreSQL.Kafka/
git commit -m "feat(samples): add Sample.PostgreSQL.Kafka app"
```

---

### Task 8: Sample.PostgreSQL.EventHub

**Files:**
- Create: `samples/Sample.PostgreSQL.EventHub/Sample.PostgreSQL.EventHub.csproj`
- Create: `samples/Sample.PostgreSQL.EventHub/Program.cs`
- Create: `samples/Sample.PostgreSQL.EventHub/PostgreSqlEventProducer.cs`
- Create: `samples/Sample.PostgreSQL.EventHub/appsettings.json`
- Create: `samples/Sample.PostgreSQL.EventHub/Dockerfile`

Same pattern as Task 7 but wires `outbox.UseEventHub()` instead of `outbox.UseKafka()`, registers `EventHubConsumer`.

- [ ] **Step 1: Create all files**

Combines PostgreSQL connection (from Task 7) with EventHub transport (from Task 6).

- [ ] **Step 2: Build and commit**

```bash
dotnet build samples/Sample.PostgreSQL.EventHub/Sample.PostgreSQL.EventHub.csproj
git add samples/Sample.PostgreSQL.EventHub/
git commit -m "feat(samples): add Sample.PostgreSQL.EventHub app"
```

---

## Chunk 3: Final Verification

### Task 9: Build All & Smoke Test

- [ ] **Step 1: Build all sample apps**

```bash
dotnet build samples/DbInit/DbInit.csproj
dotnet build samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj
dotnet build samples/Sample.SqlServer.EventHub/Sample.SqlServer.EventHub.csproj
dotnet build samples/Sample.PostgreSQL.Kafka/Sample.PostgreSQL.Kafka.csproj
dotnet build samples/Sample.PostgreSQL.EventHub/Sample.PostgreSQL.EventHub.csproj
```

Expected: All builds succeed.

- [ ] **Step 2: Docker compose build**

```bash
cd /home/vgmello/repos/poc/poc/samples
docker compose build
```

Expected: All images build successfully.

- [ ] **Step 3: Docker compose up**

```bash
cd /home/vgmello/repos/poc/poc/samples
docker compose up -d
```

Wait ~30s for everything to start, then check logs:

```bash
docker compose logs sample-sqlserver-kafka --tail 20
docker compose logs sample-postgresql-kafka --tail 20
docker compose logs sample-sqlserver-eventhub --tail 20
docker compose logs sample-postgresql-eventhub --tail 20
```

Expected log lines per app:
1. `EventProducer: Inserted {count} events for customer-{N}`
2. `Outbox publisher registered as {producerId}`
3. `Consumer: Received event from orders/{partitionKey}`

- [ ] **Step 4: Docker compose down**

```bash
docker compose down -v
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(samples): final build verification"
```
