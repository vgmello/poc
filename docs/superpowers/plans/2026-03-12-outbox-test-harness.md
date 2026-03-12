# Outbox Test Harness Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a self-contained Docker test environment with two .NET 10 console apps (EventProducer and OutboxPublisher) targeting Redpanda via Confluent.Kafka, backed by Azure SQL Edge on ARM.

**Architecture:** Monorepo under `test/` with a shared library for outbox types. EventProducer inserts synthetic events into the outbox table. OutboxPublisher leases rows and publishes to Redpanda. Docker Compose orchestrates Azure SQL Edge, Redpanda, DB init, and both apps.

**Tech Stack:** .NET 10, Confluent.Kafka, Microsoft.Data.SqlClient, Azure SQL Edge, Redpanda, Docker Compose

---

## Chunk 1: Infrastructure & Shared Library

### Task 1: Docker Compose and Init DB

**Files:**
- Create: `test/docker-compose.yml`
- Create: `test/.env`
- Create: `test/init-db/init.sql`

- [ ] **Step 1: Create `.env` file**

```env
SA_PASSWORD=OutboxTest123!
```

- [ ] **Step 2: Create `init.sql`**

Adapt from `EventHubOutbox.sql` sections 1–3 plus partition seeding (section 6a). Wrap in `CREATE DATABASE` + `USE`. Uses `GO` batch separators (required by `sqlcmd -i`). All DDL is guarded with `IF OBJECT_ID(...) IS NULL` for idempotency. Seed 8 partition buckets.

> **Note:** The `sqlcmd` path on Azure SQL Edge ARM64 may be `/opt/mssql-tools/bin/sqlcmd` or `/opt/mssql-tools18/bin/sqlcmd` depending on the image version. Verify with `docker run --rm mcr.microsoft.com/azure-sql-edge:latest find / -name sqlcmd 2>/dev/null` and update the healthcheck and init entrypoint accordingly.

```sql
IF DB_ID('OutboxTest') IS NULL
    CREATE DATABASE OutboxTest;
GO

USE OutboxTest;
GO

IF OBJECT_ID('dbo.Outbox') IS NULL
BEGIN
    CREATE TABLE dbo.Outbox
    (
        SequenceNumber  BIGINT IDENTITY(1,1)  NOT NULL,
        TopicName       NVARCHAR(256)         NOT NULL,
        PartitionKey    NVARCHAR(256)         NOT NULL,
        EventType       NVARCHAR(256)         NOT NULL,
        Headers         NVARCHAR(4000)        NULL,
        Payload         NVARCHAR(4000)        NOT NULL,
        CreatedAtUtc    DATETIME2(3)          NOT NULL  DEFAULT SYSUTCDATETIME(),
        LeasedUntilUtc  DATETIME2(3)          NULL,
        LeaseOwner      NVARCHAR(128)         NULL,
        RetryCount      INT                   NOT NULL  DEFAULT 0,
        CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (SequenceNumber)
    );
END;
GO

IF OBJECT_ID('dbo.OutboxDeadLetter') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxDeadLetter
    (
        DeadLetterSeq    BIGINT IDENTITY(1,1)  NOT NULL,
        SequenceNumber   BIGINT                NOT NULL,
        TopicName        NVARCHAR(256)         NOT NULL,
        PartitionKey     NVARCHAR(256)         NOT NULL,
        EventType        NVARCHAR(256)         NOT NULL,
        Headers          NVARCHAR(4000)        NULL,
        Payload          NVARCHAR(4000)        NOT NULL,
        CreatedAtUtc     DATETIME2(3)          NOT NULL,
        RetryCount       INT                   NOT NULL,
        DeadLetteredAtUtc DATETIME2(3)         NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastError        NVARCHAR(2000)        NULL,
        CONSTRAINT PK_OutboxDeadLetter PRIMARY KEY CLUSTERED (DeadLetterSeq)
    );
END;
GO

IF OBJECT_ID('dbo.OutboxProducers') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxProducers
    (
        ProducerId        NVARCHAR(128)  NOT NULL,
        RegisteredAtUtc   DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastHeartbeatUtc  DATETIME2(3)   NOT NULL  DEFAULT SYSUTCDATETIME(),
        HostName          NVARCHAR(256)  NULL,
        CONSTRAINT PK_OutboxProducers PRIMARY KEY CLUSTERED (ProducerId)
    );
END;
GO

IF OBJECT_ID('dbo.OutboxPartitions') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxPartitions
    (
        PartitionId        INT            NOT NULL,
        OwnerProducerId    NVARCHAR(128)  NULL,
        OwnedSinceUtc      DATETIME2(3)   NULL,
        GraceExpiresUtc    DATETIME2(3)   NULL,
        CONSTRAINT PK_OutboxPartitions PRIMARY KEY CLUSTERED (PartitionId)
    );
END;
GO

IF TYPE_ID('dbo.SequenceNumberList') IS NULL
    CREATE TYPE dbo.SequenceNumberList AS TABLE
    (
        SequenceNumber BIGINT NOT NULL PRIMARY KEY
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Outbox_Unleased')
    CREATE NONCLUSTERED INDEX IX_Outbox_Unleased
    ON dbo.Outbox (SequenceNumber)
    INCLUDE (TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
    WHERE LeasedUntilUtc IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Outbox_LeaseExpiry')
    CREATE NONCLUSTERED INDEX IX_Outbox_LeaseExpiry
    ON dbo.Outbox (LeasedUntilUtc, SequenceNumber)
    INCLUDE (TopicName, PartitionKey, EventType, Headers, Payload, RetryCount, CreatedAtUtc)
    WHERE LeasedUntilUtc IS NOT NULL;
GO

-- Seed 8 work-distribution buckets (independent of Redpanda topic partition count)
DECLARE @i INT = 0;
WHILE @i < 8
BEGIN
    INSERT INTO dbo.OutboxPartitions (PartitionId, OwnerProducerId, OwnedSinceUtc, GraceExpiresUtc)
    SELECT @i, NULL, NULL, NULL
    WHERE NOT EXISTS (SELECT 1 FROM dbo.OutboxPartitions WHERE PartitionId = @i);
    SET @i = @i + 1;
END;
GO
```

- [ ] **Step 3: Create `docker-compose.yml`**

```yaml
services:
  sqlserver:
    image: mcr.microsoft.com/azure-sql-edge:latest
    platform: linux/arm64
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "${SA_PASSWORD}"
    ports:
      - "1433:1433"
    healthcheck:
      test: /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "$${SA_PASSWORD}" -Q "SELECT 1" -b
      interval: 5s
      timeout: 5s
      retries: 20
    networks:
      - outbox-net

  sqlserver-init:
    image: mcr.microsoft.com/azure-sql-edge:latest
    platform: linux/arm64
    depends_on:
      sqlserver:
        condition: service_healthy
    volumes:
      - ./init-db/init.sql:/init.sql:ro
    entrypoint: /opt/mssql-tools/bin/sqlcmd
    command: -S sqlserver -U sa -P "${SA_PASSWORD}" -i /init.sql
    networks:
      - outbox-net

  redpanda:
    image: redpandadata/redpanda:v24.2.7
    platform: linux/arm64
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

  event-producer:
    build:
      context: .
      dockerfile: EventProducer/Dockerfile
    platform: linux/arm64
    environment:
      ConnectionStrings__OutboxDb: "Server=sqlserver;Database=OutboxTest;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=true"
    depends_on:
      sqlserver-init:
        condition: service_completed_successfully
    restart: on-failure
    networks:
      - outbox-net

  outbox-publisher:
    build:
      context: .
      dockerfile: OutboxPublisher/Dockerfile
    platform: linux/arm64
    environment:
      ConnectionStrings__OutboxDb: "Server=sqlserver;Database=OutboxTest;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=true"
      Kafka__BootstrapServers: "redpanda:9092"
    depends_on:
      sqlserver-init:
        condition: service_completed_successfully
      redpanda:
        condition: service_healthy
    restart: on-failure
    networks:
      - outbox-net

networks:
  outbox-net:
    driver: bridge
```

- [ ] **Step 4: Verify compose config parses**

Run: `cd test && docker compose config --quiet`
Expected: exits 0, no errors

- [ ] **Step 5: Commit**

```bash
git add test/docker-compose.yml test/.env test/init-db/init.sql
git commit -m "feat: add Docker Compose infrastructure and init SQL"
```

---

### Task 2: Shared Library

**Files:**
- Create: `test/Shared/Shared.csproj`
- Create: `test/Shared/OutboxRow.cs`
- Create: `test/Shared/DbHelpers.cs`

- [ ] **Step 1: Create `Shared.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.0.1" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `OutboxRow.cs`**

Match the existing `OutboxRow` record from `OutboxPublisher.cs:1224-1231`:

```csharp
namespace Outbox.Shared;

public sealed record OutboxRow(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    int RetryCount = 0);
```

- [ ] **Step 3: Create `DbHelpers.cs`**

```csharp
using System.Data;
using Microsoft.Data.SqlClient;

namespace Outbox.Shared;

public static class DbHelpers
{
    public static async Task<SqlConnection> OpenConnectionAsync(
        string connectionString, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                return conn;
            }
            catch (SqlException ex) when (IsTransientSqlError(ex) && attempt < 5)
            {
                await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task InsertOutboxRowAsync(
        SqlConnection conn,
        string topicName,
        string partitionKey,
        string eventType,
        string? headers,
        string payload,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Headers, Payload)
VALUES (@TopicName, @PartitionKey, @EventType, @Headers, @Payload);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TopicName", topicName);
        cmd.Parameters.AddWithValue("@PartitionKey", partitionKey);
        cmd.Parameters.AddWithValue("@EventType", eventType);
        cmd.Parameters.Add("@Headers", SqlDbType.NVarChar, 4000).Value =
            (object?)headers ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@Payload", payload);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static DataTable BuildSequenceNumberTvp(IEnumerable<long> sequenceNumbers)
    {
        var dt = new DataTable();
        dt.Columns.Add("SequenceNumber", typeof(long));
        foreach (var sn in sequenceNumbers)
            dt.Rows.Add(sn);
        return dt;
    }

    public static bool IsTransientSqlError(SqlException ex)
        => ex.Number is 1205 or -2 or 40613 or 40197 or 40501
            or 49918 or 49919 or 49920;
}
```

- [ ] **Step 4: Verify build**

Run: `cd test && dotnet build Shared/Shared.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add test/Shared/
git commit -m "feat: add shared library with OutboxRow and DbHelpers"
```

---

## Chunk 2: EventProducer

### Task 3: EventProducer Console App

**Files:**
- Create: `test/EventProducer/EventProducer.csproj`
- Create: `test/EventProducer/Program.cs`
- Create: `test/EventProducer/appsettings.json`
- Create: `test/EventProducer/Dockerfile`

- [ ] **Step 1: Create `EventProducer.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Shared/Shared.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `appsettings.json`**

```json
{
  "TopicName": "orders",
  "BatchSize": 5,
  "IntervalSeconds": 2,
  "PartitionKeyCount": 10
}
```

- [ ] **Step 3: Create `Program.cs`**

```csharp
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Outbox.Shared;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OutboxDb")
    ?? throw new InvalidOperationException("ConnectionStrings__OutboxDb not set");

// Load config from appsettings.json
var config = JsonSerializer.Deserialize<JsonElement>(
    await File.ReadAllTextAsync("appsettings.json"));
var topicName = config.GetProperty("TopicName").GetString()!;
var batchSize = config.GetProperty("BatchSize").GetInt32();
var intervalSeconds = config.GetProperty("IntervalSeconds").GetInt32();
var partitionKeyCount = config.GetProperty("PartitionKeyCount").GetInt32();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine($"[EventProducer] Starting — topic={topicName}, batch={batchSize}, interval={intervalSeconds}s, keys={partitionKeyCount}");

// Wait for DB to be reachable
Console.WriteLine("[EventProducer] Waiting for database...");
await using var initConn = await DbHelpers.OpenConnectionAsync(connectionString, cts.Token);
await initConn.CloseAsync();
Console.WriteLine("[EventProducer] Database is reachable.");

var random = new Random();
string[] eventTypes = ["OrderCreated", "OrderUpdated"];

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        await using var conn = await DbHelpers.OpenConnectionAsync(connectionString, cts.Token);

        for (int i = 0; i < batchSize; i++)
        {
            var partitionKey = $"customer-{random.Next(1, partitionKeyCount + 1)}";
            var eventType = eventTypes[random.Next(eventTypes.Length)];
            var payload = JsonSerializer.Serialize(new
            {
                orderId = Guid.NewGuid().ToString("N"),
                amount = Math.Round(random.NextDouble() * 1000, 2),
                timestamp = DateTime.UtcNow.ToString("O")
            });

            await DbHelpers.InsertOutboxRowAsync(
                conn, topicName, partitionKey, eventType, null, payload, cts.Token);
        }

        Console.WriteLine($"[EventProducer] Inserted {batchSize} events at {DateTime.UtcNow:HH:mm:ss.fff}");
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token);
    }
    catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[EventProducer] Error: {ex.Message}");
        await Task.Delay(2000, cts.Token);
    }
}

Console.WriteLine("[EventProducer] Shutting down.");
```

- [ ] **Step 4: Create `Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Shared/Shared.csproj Shared/
COPY EventProducer/EventProducer.csproj EventProducer/
RUN dotnet restore EventProducer/EventProducer.csproj
COPY Shared/ Shared/
COPY EventProducer/ EventProducer/
RUN dotnet publish EventProducer/EventProducer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "EventProducer.dll"]
```

- [ ] **Step 5: Verify build**

Run: `cd test && dotnet build EventProducer/EventProducer.csproj`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add test/EventProducer/
git commit -m "feat: add EventProducer console app"
```

---

## Chunk 3: OutboxPublisher (Kafka)

### Task 4: OutboxPublisher Console App — Options and Core Structure

**Files:**
- Create: `test/OutboxPublisher/OutboxPublisher.csproj`
- Create: `test/OutboxPublisher/KafkaOutboxPublisher.cs`
- Create: `test/OutboxPublisher/KafkaOutboxPublisherOptions.cs`

- [ ] **Step 1: Create `OutboxPublisher.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Shared/Shared.csproj" />
    <PackageReference Include="Confluent.Kafka" Version="2.8.0" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `KafkaOutboxPublisherOptions.cs`**

```csharp
namespace Outbox.Publisher;

public sealed class KafkaOutboxPublisherOptions
{
    public int BatchSize { get; set; } = 100;
    public int LeaseDurationSeconds { get; set; } = 45;
    public int MaxRetryCount { get; set; } = 5;
    public int MinPollIntervalMs { get; set; } = 100;
    public int MaxPollIntervalMs { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 10_000;
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int PartitionGracePeriodSeconds { get; set; } = 60;
    public int DeadLetterSweepIntervalMs { get; set; } = 60_000;
    public int OrphanSweepIntervalMs { get; set; } = 60_000;
    public int RebalanceIntervalMs { get; set; } = 30_000;
    public int KafkaProduceTimeoutSeconds { get; set; } = 15;
    public int CircuitBreakerFailureThreshold { get; set; } = 3;
    public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;
    public int SqlCommandTimeoutSeconds { get; set; } = 30;
    public Action<string, Exception?>? OnError { get; set; }
}
```

- [ ] **Step 3: Create `KafkaOutboxPublisher.cs` — fields, constructor, lifecycle**

Port from existing `OutboxPublisher.cs` with Kafka-specific changes. Full implementation:

```csharp
using System.Data;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Data.SqlClient;
using Outbox.Shared;

namespace Outbox.Publisher;

public sealed class KafkaOutboxPublisher : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _bootstrapServers;
    private readonly string _producerId;
    private readonly string _hostName;
    private readonly KafkaOutboxPublisherOptions _options;

    private IProducer<string, string>? _kafkaProducer;

    private CancellationTokenSource _cts = new();
    private Task _publishLoop = Task.CompletedTask;
    private Task _heartbeatLoop = Task.CompletedTask;
    private Task _deadLetterSweepLoop = Task.CompletedTask;
    private Task _rebalanceLoop = Task.CompletedTask;
    private Task _orphanSweepLoop = Task.CompletedTask;

    private int _totalPartitionCount;

    private volatile int _consecutiveEmptyPolls;
    private volatile int _currentPollIntervalMs;
    private volatile string? _lastPublishError;

    private readonly Dictionary<string, int> _topicFailureCount = new();
    private readonly Dictionary<string, DateTime> _topicCircuitOpenUntil = new();
    private readonly object _circuitLock = new();

    private bool _disposed;

    public KafkaOutboxPublisher(
        string connectionString,
        string bootstrapServers,
        KafkaOutboxPublisherOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString));
        if (string.IsNullOrWhiteSpace(bootstrapServers))
            throw new ArgumentNullException(nameof(bootstrapServers));

        _connectionString = connectionString;
        _bootstrapServers = bootstrapServers;
        _options = options ?? new KafkaOutboxPublisherOptions();
        _producerId = $"{Environment.MachineName}:{System.Diagnostics.Process.GetCurrentProcess().Id}:{Guid.NewGuid():N}";
        _hostName = Environment.MachineName;
        _currentPollIntervalMs = _options.MinPollIntervalMs;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_options.PartitionGracePeriodSeconds <= _options.LeaseDurationSeconds)
            throw new InvalidOperationException(
                $"PartitionGracePeriodSeconds ({_options.PartitionGracePeriodSeconds}) must exceed LeaseDurationSeconds ({_options.LeaseDurationSeconds}).");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _kafkaProducer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = _bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 3,
            RetryBackoffMs = 500,
            LingerMs = 5,
        }).Build();

        await RegisterProducerAsync(_cts.Token).ConfigureAwait(false);

        _totalPartitionCount = await GetTotalPartitionCountAsync(_cts.Token).ConfigureAwait(false);
        if (_totalPartitionCount == 0)
            throw new InvalidOperationException(
                "dbo.OutboxPartitions is empty. Run init.sql before starting publishers.");

        await RebalanceAsync(_cts.Token).ConfigureAwait(false);

        _heartbeatLoop = HeartbeatLoopAsync(_cts.Token);
        _rebalanceLoop = RebalanceLoopAsync(_cts.Token);
        _publishLoop = PublishLoopAsync(_cts.Token);
        _deadLetterSweepLoop = DeadLetterSweepLoopAsync(_cts.Token);
        _orphanSweepLoop = OrphanSweepLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();

        try
        {
            await Task.WhenAll(
                _publishLoop,
                _heartbeatLoop,
                _deadLetterSweepLoop,
                _rebalanceLoop,
                _orphanSweepLoop).ConfigureAwait(false);
        }
        finally
        {
            await UnregisterProducerAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await StopAsync().ConfigureAwait(false); }
        catch { /* best effort */ }

        _kafkaProducer?.Dispose();
        _cts.Dispose();
    }

    // -------------------------------------------------------------------------
    // Background loops — ported from OutboxPublisher.cs
    // -------------------------------------------------------------------------

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = await LeaseBatchAsync(ct).ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    _consecutiveEmptyPolls++;
                    _currentPollIntervalMs = Math.Min(
                        _currentPollIntervalMs * 2,
                        _options.MaxPollIntervalMs);
                    await Task.Delay(_currentPollIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                _consecutiveEmptyPolls = 0;
                _currentPollIntervalMs = _options.MinPollIntervalMs;

                var poison = batch.Where(r => r.RetryCount >= _options.MaxRetryCount).ToList();
                foreach (var row in poison)
                {
                    try
                    {
                        await DeadLetterSingleRowAsync(row, _lastPublishError ?? "MaxRetryCount exceeded", ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OnError($"InlineDeadLetter(Seq={row.SequenceNumber})", ex);
                    }
                }

                var publishable = poison.Count > 0
                    ? batch.Where(r => r.RetryCount < _options.MaxRetryCount).ToList()
                    : batch;

                if (publishable.Count > 0)
                    await PublishBatchAsync(publishable, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError("PublishLoop", ex);
                await Task.Delay(_options.MinPollIntervalMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatIntervalMs, ct).ConfigureAwait(false);
                await RefreshHeartbeatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("HeartbeatLoop", ex); }
        }
    }

    private async Task RebalanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RebalanceIntervalMs, ct).ConfigureAwait(false);
                await RebalanceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("RebalanceLoop", ex); }
        }
    }

    private async Task OrphanSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.OrphanSweepIntervalMs, ct).ConfigureAwait(false);
                await ClaimOrphanPartitionsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("OrphanSweepLoop", ex); }
        }
    }

    private async Task DeadLetterSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.DeadLetterSweepIntervalMs, ct).ConfigureAwait(false);
                await SweepDeadLettersAsync(_lastPublishError, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnError("DeadLetterSweepLoop", ex); }
        }
    }

    // -------------------------------------------------------------------------
    // Kafka publish
    // -------------------------------------------------------------------------

    private async Task PublishBatchAsync(IReadOnlyList<OutboxRow> rows, CancellationToken ct)
    {
        var byTopicAndKey = rows.GroupBy(r => (r.TopicName, r.PartitionKey));
        var published = new List<long>(rows.Count);

        foreach (var group in byTopicAndKey)
        {
            string topicName = group.Key.TopicName;
            string partitionKey = group.Key.PartitionKey;

            if (IsCircuitOpen(topicName))
            {
                OnError($"Circuit open for topic '{topicName}', releasing leased rows", null);
                await ReleaseLeasedRowsAsync(group.Select(r => r.SequenceNumber), ct)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                // All-or-nothing per (topic, key) group: only delete if every
                // produce succeeds. Preserves ordering invariant.
                var groupIds = new List<long>();
                bool allSucceeded = true;

                foreach (var row in group)
                {
                    var message = BuildKafkaMessage(row);

                    using var produceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    produceCts.CancelAfter(TimeSpan.FromSeconds(_options.KafkaProduceTimeoutSeconds));

                    try
                    {
                        // Note: Unlike EventHub's SendAsync, Kafka's ProduceAsync cancellation
                        // does not abort already-queued messages — timeout is best-effort only.
                        await _kafkaProducer!.ProduceAsync(topicName, message, produceCts.Token)
                            .ConfigureAwait(false);
                        groupIds.Add(row.SequenceNumber);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _lastPublishError = $"[{topicName}/{partitionKey}] Kafka produce timeout";
                        OnError($"Kafka produce timeout for topic '{topicName}' key '{partitionKey}'", null);
                        RecordSendFailure(topicName);
                        allSucceeded = false;
                        break;
                    }
                    catch (ProduceException<string, string> ex)
                    {
                        _lastPublishError = $"[{topicName}/{partitionKey}] {ex.Error.Reason}";
                        OnError($"Kafka produce error for topic '{topicName}' key '{partitionKey}'", ex);
                        RecordSendFailure(topicName);
                        allSucceeded = false;
                        break;
                    }
                }

                if (allSucceeded)
                {
                    RecordSendSuccess(topicName);
                    published.AddRange(groupIds);
                }
            }
            catch (Exception ex)
            {
                _lastPublishError = $"[{topicName}/{partitionKey}] {ex.GetType().Name}: {ex.Message}";
                OnError($"Kafka publish error for topic '{topicName}' key '{partitionKey}'", ex);
                RecordSendFailure(topicName);
            }
        }

        if (published.Count > 0)
            await DeletePublishedRowsAsync(published, ct).ConfigureAwait(false);
    }

    private static Message<string, string> BuildKafkaMessage(OutboxRow row)
    {
        var headers = new Headers();
        headers.Add("event-type", Encoding.UTF8.GetBytes(row.EventType));

        if (row.Headers is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers);
                if (parsed is not null)
                {
                    foreach (var (key, value) in parsed)
                        headers.Add(key, Encoding.UTF8.GetBytes(value));
                }
            }
            catch (JsonException)
            {
                // Malformed headers: skip, event-type header is still present
            }
        }

        return new Message<string, string>
        {
            Key = row.PartitionKey,
            Value = row.Payload,
            Headers = headers
        };
    }

    // -------------------------------------------------------------------------
    // SQL operations — identical to existing OutboxPublisher.cs
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<OutboxRow>> LeaseBatchAsync(CancellationToken ct)
    {
        const string sql = @"
WITH Batch AS
(
    SELECT TOP (@BatchSize)
        o.SequenceNumber,
        o.LeasedUntilUtc,
        o.LeaseOwner,
        o.RetryCount
    FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
    INNER JOIN dbo.OutboxPartitions op
        ON  op.OwnerProducerId = @PublisherId
        AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
        AND (ABS(CHECKSUM(o.PartitionKey)) % @TotalPartitions) = op.PartitionId
    WHERE (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME())
      AND o.RetryCount < @MaxRetryCount
    ORDER BY o.SequenceNumber
)
UPDATE Batch
SET    LeasedUntilUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()),
       LeaseOwner     = @PublisherId,
       RetryCount     = CASE WHEN LeasedUntilUtc IS NOT NULL
                             THEN RetryCount + 1
                             ELSE RetryCount END
OUTPUT inserted.SequenceNumber,
       inserted.TopicName,
       inserted.PartitionKey,
       inserted.EventType,
       inserted.Headers,
       inserted.Payload,
       inserted.RetryCount;";

        int totalPartitions = _totalPartitionCount;
        if (totalPartitions == 0) return [];

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@BatchSize", _options.BatchSize);
                cmd.Parameters.AddWithValue("@LeaseDurationSeconds", _options.LeaseDurationSeconds);
                cmd.Parameters.AddWithValue("@PublisherId", _producerId);
                cmd.Parameters.AddWithValue("@TotalPartitions", totalPartitions);
                cmd.Parameters.AddWithValue("@MaxRetryCount", _options.MaxRetryCount);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;

                var rows = new List<OutboxRow>();
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add(new OutboxRow(
                        SequenceNumber: reader.GetInt64(0),
                        TopicName: reader.GetString(1),
                        PartitionKey: reader.GetString(2),
                        EventType: reader.GetString(3),
                        Headers: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Payload: reader.GetString(5),
                        RetryCount: reader.GetInt32(6)));
                }

                return rows;
            }
            catch (SqlException ex) when (DbHelpers.IsTransientSqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DeletePublishedRowsAsync(IEnumerable<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
DELETE o
FROM   dbo.Outbox o
INNER JOIN @PublishedIds p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new SqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@PublisherId", _producerId);

                var tvp = DbHelpers.BuildSequenceNumberTvp(sequenceNumbers);
                var tvpParam = cmd.Parameters.AddWithValue("@PublishedIds", tvp);
                tvpParam.SqlDbType = SqlDbType.Structured;
                tvpParam.TypeName = "dbo.SequenceNumberList";

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (DbHelpers.IsTransientSqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseLeasedRowsAsync(IEnumerable<long> sequenceNumbers, CancellationToken ct)
    {
        const string sql = @"
UPDATE o
SET    o.LeasedUntilUtc = NULL,
       o.LeaseOwner     = NULL
FROM   dbo.Outbox o
INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber
WHERE  o.LeaseOwner = @PublisherId;";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await using var cmd = new SqlCommand(sql, conn);
                cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@PublisherId", _producerId);

                var tvp = DbHelpers.BuildSequenceNumberTvp(sequenceNumbers);
                var tvpParam = cmd.Parameters.AddWithValue("@Ids", tvp);
                tvpParam.SqlDbType = SqlDbType.Structured;
                tvpParam.TypeName = "dbo.SequenceNumberList";

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (DbHelpers.IsTransientSqlError(ex) && attempt < 3)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SweepDeadLettersAsync(string? lastError, CancellationToken ct)
    {
        const string sql = @"
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.CreatedAtUtc, deleted.RetryCount, SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o WITH (ROWLOCK, READPAST)
WHERE o.RetryCount >= @MaxRetryCount
  AND (o.LeasedUntilUtc IS NULL OR o.LeasedUntilUtc < SYSUTCDATETIME());";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@MaxRetryCount", _options.MaxRetryCount);
        cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
            (object?)lastError ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task DeadLetterSingleRowAsync(OutboxRow row, string reason, CancellationToken ct)
    {
        const string sql = @"
DELETE o
OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
       deleted.EventType, deleted.Headers, deleted.Payload,
       deleted.CreatedAtUtc, deleted.RetryCount, SYSUTCDATETIME(), @LastError
INTO dbo.OutboxDeadLetter(SequenceNumber, TopicName, PartitionKey, EventType,
     Headers, Payload, CreatedAtUtc, RetryCount, DeadLetteredAtUtc, LastError)
FROM dbo.Outbox o
WHERE o.SequenceNumber = @SequenceNumber
  AND o.LeaseOwner = @PublisherId;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@SequenceNumber", row.SequenceNumber);
        cmd.Parameters.AddWithValue("@PublisherId", _producerId);
        cmd.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
            (object?)reason ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RegisterProducerAsync(CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.OutboxProducers WITH (HOLDLOCK) AS target
USING (SELECT @ProducerId AS ProducerId, @HostName AS HostName) AS source
    ON target.ProducerId = source.ProducerId
WHEN MATCHED THEN
    UPDATE SET LastHeartbeatUtc = SYSUTCDATETIME(),
               HostName         = source.HostName
WHEN NOT MATCHED THEN
    INSERT (ProducerId, RegisteredAtUtc, LastHeartbeatUtc, HostName)
    VALUES (source.ProducerId, SYSUTCDATETIME(), SYSUTCDATETIME(), source.HostName);";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);
        cmd.Parameters.AddWithValue("@HostName", _hostName);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshHeartbeatAsync(CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.OutboxProducers
SET    LastHeartbeatUtc = SYSUTCDATETIME()
WHERE  ProducerId = @ProducerId;

UPDATE dbo.OutboxPartitions
SET    GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId
  AND  GraceExpiresUtc IS NOT NULL;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task UnregisterProducerAsync(CancellationToken ct)
    {
        const string releaseSql = @"
UPDATE dbo.OutboxPartitions
SET    OwnerProducerId = NULL,
       OwnedSinceUtc  = NULL,
       GraceExpiresUtc = NULL
WHERE  OwnerProducerId = @ProducerId;";

        const string deleteSql = @"
DELETE FROM dbo.OutboxProducers
WHERE  ProducerId = @ProducerId;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var releaseCmd = new SqlCommand(releaseSql, conn, tx);
        releaseCmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        releaseCmd.Parameters.AddWithValue("@ProducerId", _producerId);
        await releaseCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var deleteCmd = new SqlCommand(deleteSql, conn, tx);
        deleteCmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        deleteCmd.Parameters.AddWithValue("@ProducerId", _producerId);
        await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> GetTotalPartitionCountAsync(CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM dbo.OutboxPartitions;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private async Task RebalanceAsync(CancellationToken ct)
    {
        const string sql = @"
BEGIN TRANSACTION;

DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

IF @ToAcquire > 0
BEGIN
    UPDATE dbo.OutboxPartitions
    SET    GraceExpiresUtc = DATEADD(SECOND, @PartitionGracePeriodSeconds, SYSUTCDATETIME())
    WHERE  OwnerProducerId <> @ProducerId
      AND  OwnerProducerId IS NOT NULL
      AND  GraceExpiresUtc IS NULL
      AND  OwnerProducerId NOT IN
           (
               SELECT ProducerId
               FROM   dbo.OutboxProducers
               WHERE  LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME())
           );

    UPDATE TOP (@ToAcquire) dbo.OutboxPartitions WITH (UPDLOCK)
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    WHERE  (OwnerProducerId IS NULL
            OR GraceExpiresUtc < SYSUTCDATETIME());
END;

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

IF @CurrentlyOwned > @FairShare
BEGIN
    DECLARE @ToRelease INT = @CurrentlyOwned - @FairShare;

    UPDATE TOP (@ToRelease) dbo.OutboxPartitions
    SET    OwnerProducerId = NULL,
           OwnedSinceUtc  = NULL,
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId = @ProducerId;
END;

COMMIT TRANSACTION;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);
        cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _options.HeartbeatTimeoutSeconds);
        cmd.Parameters.AddWithValue("@PartitionGracePeriodSeconds", _options.PartitionGracePeriodSeconds);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task ClaimOrphanPartitionsAsync(CancellationToken ct)
    {
        const string sql = @"
DECLARE @TotalPartitions   INT;
DECLARE @ActiveProducers   INT;
DECLARE @FairShare         INT;
DECLARE @CurrentlyOwned    INT;
DECLARE @ToAcquire         INT;

SELECT @TotalPartitions = COUNT(*) FROM dbo.OutboxPartitions;

SELECT @ActiveProducers = COUNT(*)
FROM dbo.OutboxProducers
WHERE LastHeartbeatUtc >= DATEADD(SECOND, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME());

SET @FairShare = CEILING(CAST(@TotalPartitions AS FLOAT) / NULLIF(@ActiveProducers, 0));

SELECT @CurrentlyOwned = COUNT(*)
FROM dbo.OutboxPartitions
WHERE OwnerProducerId = @ProducerId;

SET @ToAcquire = @FairShare - @CurrentlyOwned;

IF @ToAcquire > 0
BEGIN
    UPDATE TOP (@ToAcquire) dbo.OutboxPartitions WITH (UPDLOCK)
    SET    OwnerProducerId = @ProducerId,
           OwnedSinceUtc   = SYSUTCDATETIME(),
           GraceExpiresUtc = NULL
    WHERE  OwnerProducerId IS NULL;
END;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = _options.SqlCommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ProducerId", _producerId);
        cmd.Parameters.AddWithValue("@HeartbeatTimeoutSeconds", _options.HeartbeatTimeoutSeconds);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Circuit breaker
    // -------------------------------------------------------------------------

    private bool IsCircuitOpen(string topicName)
    {
        lock (_circuitLock)
        {
            if (_topicCircuitOpenUntil.TryGetValue(topicName, out var openUntil))
            {
                if (DateTime.UtcNow < openUntil)
                    return true;
                _topicCircuitOpenUntil.Remove(topicName);
            }
            return false;
        }
    }

    private void RecordSendSuccess(string topicName)
    {
        lock (_circuitLock)
        {
            _topicFailureCount.Remove(topicName);
            _topicCircuitOpenUntil.Remove(topicName);
        }
    }

    private void RecordSendFailure(string topicName)
    {
        lock (_circuitLock)
        {
            _topicFailureCount.TryGetValue(topicName, out int count);
            count++;
            _topicFailureCount[topicName] = count;

            if (count >= _options.CircuitBreakerFailureThreshold)
            {
                var openUntil = DateTime.UtcNow.AddSeconds(_options.CircuitBreakerOpenDurationSeconds);
                _topicCircuitOpenUntil[topicName] = openUntil;
                OnError($"Circuit breaker OPEN for topic '{topicName}' until {openUntil:O}", null);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void OnError(string context, Exception? exception)
    {
        var handler = _options.OnError;
        if (handler is not null)
            handler(context, exception);
        else
            Console.Error.WriteLine($"[KafkaOutboxPublisher] {context}: {exception?.Message}");
    }
}
```

- [ ] **Step 4: Verify build**

Run: `cd test && dotnet build OutboxPublisher/OutboxPublisher.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add test/OutboxPublisher/OutboxPublisher.csproj test/OutboxPublisher/KafkaOutboxPublisher.cs test/OutboxPublisher/KafkaOutboxPublisherOptions.cs
git commit -m "feat: add KafkaOutboxPublisher with Confluent.Kafka"
```

---

### Task 5: OutboxPublisher Program.cs and Dockerfile

**Files:**
- Create: `test/OutboxPublisher/Program.cs`
- Create: `test/OutboxPublisher/appsettings.json`
- Create: `test/OutboxPublisher/Dockerfile`

- [ ] **Step 1: Create `appsettings.json`**

```json
{
  "BatchSize": 100,
  "LeaseDurationSeconds": 45,
  "MaxRetryCount": 5
}
```

- [ ] **Step 2: Create `Program.cs`**

```csharp
using System.Text.Json;
using Outbox.Publisher;
using Outbox.Shared;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OutboxDb")
    ?? throw new InvalidOperationException("ConnectionStrings__OutboxDb not set");
var bootstrapServers = Environment.GetEnvironmentVariable("Kafka__BootstrapServers")
    ?? throw new InvalidOperationException("Kafka__BootstrapServers not set");

var config = JsonSerializer.Deserialize<JsonElement>(
    await File.ReadAllTextAsync("appsettings.json"));

var options = new KafkaOutboxPublisherOptions
{
    BatchSize = config.GetProperty("BatchSize").GetInt32(),
    LeaseDurationSeconds = config.GetProperty("LeaseDurationSeconds").GetInt32(),
    MaxRetryCount = config.GetProperty("MaxRetryCount").GetInt32(),
    OnError = (context, ex) =>
        Console.Error.WriteLine($"[OutboxPublisher] {context}: {ex?.Message}")
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine($"[OutboxPublisher] Starting — bootstrap={bootstrapServers}, batch={options.BatchSize}");

// Wait for DB to be reachable
Console.WriteLine("[OutboxPublisher] Waiting for database...");
await using var initConn = await DbHelpers.OpenConnectionAsync(connectionString, cts.Token);
await initConn.CloseAsync();
Console.WriteLine("[OutboxPublisher] Database is reachable.");

await using var publisher = new KafkaOutboxPublisher(connectionString, bootstrapServers, options);
await publisher.StartAsync(cts.Token);

Console.WriteLine("[OutboxPublisher] Running. Press Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException) { }

// DisposeAsync (via `await using`) calls StopAsync internally — no explicit call needed.
Console.WriteLine("[OutboxPublisher] Shutting down...");
```

- [ ] **Step 3: Create `Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Shared/Shared.csproj Shared/
COPY OutboxPublisher/OutboxPublisher.csproj OutboxPublisher/
RUN dotnet restore OutboxPublisher/OutboxPublisher.csproj
COPY Shared/ Shared/
COPY OutboxPublisher/ OutboxPublisher/
RUN dotnet publish OutboxPublisher/OutboxPublisher.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OutboxPublisher.dll"]
```

- [ ] **Step 4: Verify build**

Run: `cd test && dotnet build OutboxPublisher/OutboxPublisher.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add test/OutboxPublisher/Program.cs test/OutboxPublisher/appsettings.json test/OutboxPublisher/Dockerfile
git commit -m "feat: add OutboxPublisher Program.cs and Dockerfile"
```

---

## Chunk 4: Integration Verification

### Task 6: End-to-End Docker Compose Test

- [ ] **Step 1: Build all Docker images**

Run: `cd test && docker compose build`
Expected: Both images build successfully

- [ ] **Step 2: Start infrastructure services**

Run: `cd test && docker compose up sqlserver redpanda sqlserver-init -d`
Expected: SQL Edge and Redpanda start, init completes with exit 0

- [ ] **Step 3: Verify database schema**

Run: `docker compose exec sqlserver /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P 'OutboxTest123!' -d OutboxTest -Q "SELECT COUNT(*) FROM dbo.OutboxPartitions"`
Expected: Returns 8

- [ ] **Step 4: Start all services**

Run: `cd test && docker compose up -d`
Expected: All services running

- [ ] **Step 5: Verify events flowing**

Run: `docker compose logs -f event-producer --tail 10` (wait ~10s)
Expected: Logs showing "Inserted 5 events at ..." messages

Run: `docker compose logs -f outbox-publisher --tail 10` (wait ~10s)
Expected: Logs showing publisher is running, no errors

- [ ] **Step 6: Verify Redpanda receives messages**

Run: `docker compose exec redpanda rpk topic consume orders --num 5`
Expected: 5 JSON messages with orderId, amount, timestamp fields

- [ ] **Step 7: Tear down**

Run: `cd test && docker compose down -v`

- [ ] **Step 8: Commit final state**

```bash
git add -A test/
git commit -m "feat: complete outbox test harness with Docker Compose"
```
