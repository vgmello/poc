# Outbox Integration Tests Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement integration tests for all 14 failure scenarios from `docs/failure-scenarios-and-integration-tests.md` using real PostgreSQL + Redpanda via Testcontainers.

**Architecture:** Shared xUnit collection fixture boots PostgreSQL and Redpanda containers once for the suite. Each test gets isolated topic names and truncates tables between runs. Fault injection uses decorator wrappers (FaultyTransportWrapper for broker faults, ToggleableConnectionFactory for DB faults) rather than container stop/start for speed and determinism.

**Tech Stack:** xUnit 2.x, Testcontainers.PostgreSql 4.10.0, Testcontainers.Redpanda 4.10.0, Confluent.Kafka 2.8.0, Npgsql 9.0.3, .NET 10.0

**Spec document:** `docs/failure-scenarios-and-integration-tests.md`

---

## File Structure

```
tests/Outbox.IntegrationTests/
├── Outbox.IntegrationTests.csproj
├── Fixtures/
│   ├── InfrastructureFixture.cs         -- Boots Postgres + Redpanda containers, runs schema, provides connection info
│   └── InfrastructureCollection.cs      -- xUnit collection definition
├── Helpers/
│   ├── OutboxTestHelper.cs              -- Insert messages, build hosts, consume from Kafka, cleanup, query helpers
│   ├── FaultyTransportWrapper.cs        -- IOutboxTransport decorator with togglable failures
│   └── ToggleableConnectionFactory.cs   -- Connection factory with togglable DB failures
└── Scenarios/
    ├── BrokerDownTests.cs               -- Scenario 1 + 14: broker unavailable + network partition
    ├── DatabaseDownTests.cs             -- Scenario 2: DB unavailable
    ├── ProcessKillTests.cs              -- Scenario 3: SIGKILL mid-batch
    ├── GracefulShutdownTests.cs         -- Scenario 4: SIGTERM lease release
    ├── PoisonMessageTests.cs            -- Scenario 5: oversized/malformed → dead letter
    ├── IntermittentFailureTests.cs      -- Scenario 6: flaky transport
    ├── CircuitBreakerRetryTests.cs      -- Scenario 7: circuit open doesn't burn retries
    ├── MultiPublisherRebalanceTests.cs  -- Scenario 8: scale up/down partition fairness
    ├── LoopCrashRecoveryTests.cs        -- Scenario 9: internal loop crash + restart
    ├── HealthCheckTests.cs              -- Scenario 10: health state accuracy
    ├── PendingMetricTests.cs            -- Scenario 11: pending gauge
    ├── DeadLetterReplayTests.cs         -- Scenario 12: replay from DLQ
    └── OrderingTests.cs                 -- Scenario 13: per-partition-key ordering
```

---

## Chunk 1: Foundation

### Task 1: Project Scaffolding

**Files:**
- Create: `tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj`
- Modify: `src/Outbox.slnx`

- [ ] **Step 1: Create the integration test project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.*" />
    <PackageReference Include="Testcontainers.Redpanda" Version="4.*" />
    <PackageReference Include="Confluent.Kafka" Version="2.8.0" />
    <PackageReference Include="Npgsql" Version="9.0.3" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.0" />
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Outbox.Core/Outbox.Core.csproj" />
    <ProjectReference Include="../../src/Outbox.PostgreSQL/Outbox.PostgreSQL.csproj" />
    <ProjectReference Include="../../src/Outbox.Kafka/Outbox.Kafka.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add project to solution**

Add to `src/Outbox.slnx`:
```xml
<Project Path="../tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj" />
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj src/Outbox.slnx
git commit -m "feat: add integration test project scaffold"
```

---

### Task 2: Infrastructure Fixture

**Files:**
- Create: `tests/Outbox.IntegrationTests/Fixtures/InfrastructureFixture.cs`
- Create: `tests/Outbox.IntegrationTests/Fixtures/InfrastructureCollection.cs`

- [ ] **Step 1: Create the collection definition**

```csharp
using Xunit;

namespace Outbox.IntegrationTests.Fixtures;

[CollectionDefinition(Name)]
public class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "Infrastructure";
}
```

- [ ] **Step 2: Create the infrastructure fixture**

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;
using Xunit;

namespace Outbox.IntegrationTests.Fixtures;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private readonly RedpandaContainer _redpanda = new RedpandaBuilder()
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
        // Read the install.sql from the PostgreSQL project
        var sqlPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Outbox.PostgreSQL", "db_scripts", "install.sql");
        sqlPath = Path.GetFullPath(sqlPath);

        var sql = await File.ReadAllTextAsync(sqlPath);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: Add a smoke test to verify containers boot**

Create a temporary file `tests/Outbox.IntegrationTests/Scenarios/SmokeTest.cs`:

```csharp
using Npgsql;
using Outbox.IntegrationTests.Fixtures;
using Xunit;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class SmokeTest
{
    private readonly InfrastructureFixture _infra;

    public SmokeTest(InfrastructureFixture infra) => _infra = infra;

    [Fact]
    public async Task Containers_AreRunning()
    {
        // Verify PostgreSQL
        await using var conn = new NpgsqlConnection(_infra.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM outbox_partitions", conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(32, count);

        // Verify Redpanda
        Assert.False(string.IsNullOrEmpty(_infra.BootstrapServers));
    }
}
```

- [ ] **Step 4: Run the smoke test**

Run: `dotnet test tests/Outbox.IntegrationTests --filter SmokeTest -v n`
Expected: 1 test passed. Containers boot, schema installed, 32 partitions seeded.

- [ ] **Step 5: Commit**

```bash
git add tests/Outbox.IntegrationTests/Fixtures/ tests/Outbox.IntegrationTests/Scenarios/SmokeTest.cs
git commit -m "feat: add Testcontainers infrastructure fixture with Postgres + Redpanda"
```

---

### Task 3: Test Helpers

**Files:**
- Create: `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs`

- [ ] **Step 1: Create the test helper class**

```csharp
using System.Data.Common;
using System.Diagnostics.Metrics;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Observability;
using Outbox.Core.Options;
using Outbox.Kafka;
using Outbox.PostgreSQL;

namespace Outbox.IntegrationTests.Helpers;

public sealed record ConsumedMessage(string Key, string Value, long Offset);

public static class OutboxTestHelper
{
    /// <summary>Fast publisher options for integration tests.</summary>
    public static readonly Action<OutboxPublisherOptions> FastTestOptions = o =>
    {
        o.BatchSize = 10;
        o.MaxRetryCount = 5;
        o.MinPollIntervalMs = 50;
        o.MaxPollIntervalMs = 500;
        o.LeaseDurationSeconds = 10;
        o.HeartbeatIntervalMs = 2_000;
        o.HeartbeatTimeoutSeconds = 5;
        o.PartitionGracePeriodSeconds = 8;
        o.RebalanceIntervalMs = 3_000;
        o.OrphanSweepIntervalMs = 3_000;
        o.DeadLetterSweepIntervalMs = 3_000;
        o.CircuitBreakerFailureThreshold = 3;
        o.CircuitBreakerOpenDurationSeconds = 5;
    };

    // ------------------------------------------------------------------
    // Host builder
    // ------------------------------------------------------------------

    public static (IHost Host, FaultyTransportWrapper Transport) BuildPublisherHost(
        string connectionString,
        string bootstrapServers,
        Action<OutboxPublisherOptions>? configureOptions = null,
        ToggleableConnectionFactory? connectionFactory = null)
    {
        connectionFactory ??= new ToggleableConnectionFactory(connectionString);

        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Empty section so Configure<T> binds cleanly
                    ["Outbox:Publisher:BatchSize"] = "10",
                });
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddOutbox(ctx.Configuration, outbox =>
                {
                    outbox.UsePostgreSql(connectionFactory.CreateConnectionAsync);
                    outbox.ConfigurePublisher(o =>
                    {
                        FastTestOptions(o);
                        configureOptions?.Invoke(o);
                    });
                });

                // Register Kafka producer + faulty transport wrapper (replaces TryAdd from UseKafka)
                services.AddSingleton<IProducer<string, string>>(_ =>
                    new ProducerBuilder<string, string>(new ProducerConfig
                    {
                        BootstrapServers = bootstrapServers,
                        Acks = Acks.All,
                        EnableIdempotence = true,
                        LingerMs = 5,
                    }).Build());

                services.AddSingleton<FaultyTransportWrapper>();
                services.AddSingleton<IOutboxTransport>(sp =>
                    sp.GetRequiredService<FaultyTransportWrapper>());
            })
            .Build();

        var transport = host.Services.GetRequiredService<FaultyTransportWrapper>();
        return (host, transport);
    }

    // ------------------------------------------------------------------
    // Message insertion (direct SQL — simulates the application writing to outbox)
    // ------------------------------------------------------------------

    public static async Task InsertMessagesAsync(
        string connectionString, int count, string topic,
        string? partitionKey = null, int startOrdinal = 0)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        for (int i = 0; i < count; i++)
        {
            var key = partitionKey ?? $"key-{i % 10}";
            var sql = @"
                INSERT INTO outbox (topic_name, partition_key, event_type, payload,
                                    event_datetime_utc, event_ordinal)
                VALUES (@topic, @key, 'TestEvent', @payload, clock_timestamp(), @ordinal)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@topic", topic);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@payload", $"{{\"index\":{i}}}");
            cmd.Parameters.AddWithValue("@ordinal", (short)(startOrdinal + i));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ------------------------------------------------------------------
    // Kafka consumer — collects messages from a topic
    // ------------------------------------------------------------------

    public static async Task<List<ConsumedMessage>> ConsumeMessagesAsync(
        string bootstrapServers, string topic, int expectedCount, TimeSpan timeout)
    {
        var messages = new List<ConsumedMessage>();
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + timeout;
        while (messages.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(200));
                if (result?.Message != null)
                    messages.Add(new ConsumedMessage(
                        result.Message.Key, result.Message.Value, result.Offset.Value));
            }
            catch (ConsumeException) { /* topic may not exist yet */ }
        }

        consumer.Close();
        return messages;
    }

    // ------------------------------------------------------------------
    // DB query helpers
    // ------------------------------------------------------------------

    public static async Task<long> GetOutboxCountAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM outbox", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<long> GetDeadLetterCountAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM outbox_dead_letter", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<List<(long Seq, int RetryCount)>> GetRetryCountsAsync(string connectionString)
    {
        var results = new List<(long, int)>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT sequence_number, retry_count FROM outbox ORDER BY sequence_number", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetInt64(0), reader.GetInt32(1)));
        return results;
    }

    public static async Task<List<string>> GetProducerIdsAsync(string connectionString)
    {
        var ids = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT producer_id FROM outbox_producers", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public static async Task<Dictionary<int, string?>> GetPartitionOwnersAsync(string connectionString)
    {
        var map = new Dictionary<int, string?>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT partition_id, owner_producer_id FROM outbox_partitions ORDER BY partition_id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            map[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return map;
    }

    // ------------------------------------------------------------------
    // Cleanup — called at the start of each test
    // ------------------------------------------------------------------

    public static async Task CleanupAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            TRUNCATE outbox, outbox_dead_letter, outbox_producers;
            UPDATE outbox_partitions SET owner_producer_id = NULL, owned_since_utc = NULL, grace_expires_utc = NULL;",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------
    // Wait helpers
    // ------------------------------------------------------------------

    public static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout,
        TimeSpan? pollInterval = null, string? message = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(interval);
        }
        throw new TimeoutException(message ?? $"Condition not met within {timeout}");
    }

    public static string UniqueTopic(string prefix = "test") =>
        $"{prefix}-{Guid.NewGuid():N}";
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build tests/Outbox.IntegrationTests`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs
git commit -m "feat: add integration test helper (insert, consume, query, cleanup, host builder)"
```

---

### Task 4: Fault Injection Wrappers

**Files:**
- Create: `tests/Outbox.IntegrationTests/Helpers/FaultyTransportWrapper.cs`
- Create: `tests/Outbox.IntegrationTests/Helpers/ToggleableConnectionFactory.cs`

- [ ] **Step 1: Create FaultyTransportWrapper**

```csharp
using Confluent.Kafka;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.IntegrationTests.Helpers;

/// <summary>
/// IOutboxTransport decorator that can be toggled to simulate broker failures.
/// When not failing, delegates to a real Kafka producer.
/// </summary>
public sealed class FaultyTransportWrapper : IOutboxTransport
{
    private readonly IProducer<string, string> _producer;
    private volatile bool _failing;
    private int _callCount;
    private volatile int _failEveryN; // 0 = disabled; N = fail all except every Nth call

    public FaultyTransportWrapper(IProducer<string, string> producer) => _producer = producer;

    /// <summary>Total number of SendAsync calls made.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Set to true to make all sends throw.</summary>
    public void SetFailing(bool failing) => _failing = failing;

    /// <summary>Fail all calls except every Nth. Set 0 to disable.</summary>
    public void SetIntermittent(int failEveryN) => _failEveryN = failEveryN;

    public void Reset()
    {
        _failing = false;
        _failEveryN = 0;
        Interlocked.Exchange(ref _callCount, 0);
    }

    public async Task SendAsync(
        string topicName, string partitionKey,
        IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _callCount);

        if (_failing)
            throw new InvalidOperationException("Simulated broker failure");

        var n = _failEveryN;
        if (n > 0 && count % n != 0)
            throw new InvalidOperationException($"Simulated intermittent failure (call {count}, succeeds every {n})");

        // Real send via Kafka producer
        foreach (var msg in messages)
        {
            await _producer.ProduceAsync(topicName,
                new Message<string, string>
                {
                    Key = partitionKey,
                    Value = msg.Payload,
                    Headers = new Headers
                    {
                        { "EventType", System.Text.Encoding.UTF8.GetBytes(msg.EventType) }
                    }
                },
                cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _producer.Flush(TimeSpan.FromSeconds(3)); }
        catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Create ToggleableConnectionFactory**

```csharp
using System.Data.Common;
using Npgsql;

namespace Outbox.IntegrationTests.Helpers;

/// <summary>
/// Connection factory that can be toggled to simulate DB outages.
/// </summary>
public sealed class ToggleableConnectionFactory
{
    private readonly string _connectionString;
    private volatile bool _failing;

    public ToggleableConnectionFactory(string connectionString) =>
        _connectionString = connectionString;

    public void SetFailing(bool failing) => _failing = failing;

    public Task<DbConnection> CreateConnectionAsync(IServiceProvider sp, CancellationToken ct)
    {
        if (_failing)
            throw new NpgsqlException("Simulated DB connection failure");

        return Task.FromResult<DbConnection>(new NpgsqlConnection(_connectionString));
    }
}
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build tests/Outbox.IntegrationTests`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.IntegrationTests/Helpers/FaultyTransportWrapper.cs tests/Outbox.IntegrationTests/Helpers/ToggleableConnectionFactory.cs
git commit -m "feat: add FaultyTransportWrapper and ToggleableConnectionFactory for fault injection"
```

---

## Chunk 2: Resilience Scenarios (1-4)

### Task 5: Scenario 1 — Broker Down + Scenario 14 — Network Partition

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/BrokerDownTests.cs`

- [ ] **Step 1: Write the test class**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class BrokerDownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public BrokerDownTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task BrokerDown_MessagesAccumulate_ThenDrainOnRecovery()
    {
        // Arrange
        var topic = OutboxTestHelper.UniqueTopic("broker-down");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await host.StartAsync();

            // Phase 1: Publish some messages successfully
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(15), message: "Initial messages should drain");

            var initialMessages = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 10, TimeSpan.FromSeconds(5));
            Assert.Equal(10, initialMessages.Count);

            // Phase 2: Block broker, insert more messages
            transport.SetFailing(true);
            _output.WriteLine("Broker set to failing");

            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic, "key-1");
            await Task.Delay(TimeSpan.FromSeconds(8)); // Allow circuit breaker to open

            // Assert: messages still in outbox
            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            Assert.True(pending > 0, $"Messages should accumulate in outbox, got {pending}");
            _output.WriteLine($"Pending messages during outage: {pending}");

            // Phase 3: Restore broker
            transport.SetFailing(false);
            _output.WriteLine("Broker restored");

            // Wait for circuit to half-open and messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain after broker recovery");

            // Assert: all messages consumed (10 initial + 50 during outage, possibly with duplicates)
            var allMessages = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 60, TimeSpan.FromSeconds(10));
            Assert.True(allMessages.Count >= 50,
                $"Expected at least 50 new messages, got {allMessages.Count} total (includes initial 10 + possible duplicates)");

            // Assert: outbox is empty
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task NetworkPartition_DbReachable_BrokerNot_CircuitOpensAndRecovers()
    {
        // Scenario 14: DB works, broker doesn't
        var topic = OutboxTestHelper.UniqueTopic("net-partition");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await host.StartAsync();

            // Block broker only (DB stays up)
            transport.SetFailing(true);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, topic, "key-1");
            await Task.Delay(TimeSpan.FromSeconds(8));

            // Assert: messages accumulate, heartbeat still works (DB is fine)
            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            Assert.True(pending > 0, "Messages should accumulate");

            // Producers should still be registered (heartbeat works)
            var producers = await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString);
            Assert.Single(producers);

            // Restore broker
            transport.SetFailing(false);

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain after broker recovery");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Outbox.IntegrationTests --filter BrokerDownTests -v n`
Expected: 2 tests passed

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/BrokerDownTests.cs
git commit -m "test: add Scenario 1 (broker down) and Scenario 14 (network partition) integration tests"
```

---

### Task 6: Scenario 2 — Database Down

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/DatabaseDownTests.cs`

- [ ] **Step 1: Write the test class**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class DatabaseDownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public DatabaseDownTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task DatabaseDown_PublisherSurvives_AndRecoversAutomatically()
    {
        var topic = OutboxTestHelper.UniqueTopic("db-down");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var connFactory = new ToggleableConnectionFactory(_infra.ConnectionString);
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            connectionFactory: connFactory);

        try
        {
            await host.StartAsync();

            // Insert and publish some messages successfully
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(15), message: "Initial messages should drain");

            // Block DB
            connFactory.SetFailing(true);
            _output.WriteLine("DB set to failing");

            // Wait a bit — publisher should survive (no crash, no unhandled exception)
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Restore DB
            connFactory.SetFailing(false);
            _output.WriteLine("DB restored");

            // Insert more messages
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, topic, "key-1");

            // Wait for new messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "Messages should drain after DB recovery");

            // Verify messages arrived at broker
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 30, TimeSpan.FromSeconds(10));
            Assert.True(consumed.Count >= 30,
                $"Expected at least 30 messages consumed, got {consumed.Count}");

            // Verify producer is still registered
            var producers = await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString);
            Assert.Single(producers);
        }
        finally
        {
            connFactory.SetFailing(false);
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Outbox.IntegrationTests --filter DatabaseDownTests -v n`
Expected: 1 test passed

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/DatabaseDownTests.cs
git commit -m "test: add Scenario 2 (database down) integration test"
```

---

### Task 7: Scenario 3 — Process Kill + Scenario 4 — Graceful Shutdown

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/ProcessKillTests.cs`
- Create: `tests/Outbox.IntegrationTests/Scenarios/GracefulShutdownTests.cs`

- [ ] **Step 1: Write ProcessKillTests**

```csharp
using Npgsql;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class ProcessKillTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public ProcessKillTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task SigKill_SurvivingPublisher_ClaimsOrphanedPartitions_AndDrainsMessages()
    {
        var topic = OutboxTestHelper.UniqueTopic("sigkill");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publisher A
        var (hostA, transportA) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        // Wait for A to register and claim partitions
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
            return owners.Values.Any(v => v != null);
        }, TimeSpan.FromSeconds(10), message: "Publisher A should claim partitions");

        var producerIdA = (await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString)).First();
        _output.WriteLine($"Publisher A registered as {producerIdA}");

        // Insert messages and let A process some
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic, "key-1");
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Simulate SIGKILL: stop A gracefully (which unregisters), then re-insert stale producer
        await hostA.StopAsync();
        hostA.Dispose();

        // Re-create the stale producer row and assign partitions (simulating no cleanup)
        await using (var conn = new NpgsqlConnection(_infra.ConnectionString))
        {
            await conn.OpenAsync();

            // Re-insert stale producer with old heartbeat
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO outbox_producers (producer_id, registered_at_utc, last_heartbeat_utc, host_name)
                VALUES (@id, clock_timestamp() - interval '5 minutes', clock_timestamp() - interval '5 minutes', 'dead-host')
                ON CONFLICT (producer_id) DO UPDATE SET last_heartbeat_utc = clock_timestamp() - interval '5 minutes'",
                conn);
            insertCmd.Parameters.AddWithValue("@id", producerIdA);
            await insertCmd.ExecuteNonQueryAsync();

            // Assign half the partitions to the dead producer
            await using var assignCmd = new NpgsqlCommand(@"
                UPDATE outbox_partitions SET owner_producer_id = @id, owned_since_utc = clock_timestamp()
                WHERE partition_id < 16", conn);
            assignCmd.Parameters.AddWithValue("@id", producerIdA);
            await assignCmd.ExecuteNonQueryAsync();
        }

        // Re-insert any remaining messages
        var remaining = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
        if (remaining == 0)
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, topic, "key-1");

        // Start publisher B
        var (hostB, transportB) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await hostB.StartAsync();

            // Wait for B to detect stale A and claim partitions
            // HeartbeatTimeout=5s, GracePeriod=8s, RebalanceInterval=3s → ~16s worst case
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
                var producerB = (await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString))
                    .FirstOrDefault(id => id != producerIdA);
                if (producerB == null) return false;
                return owners.Values.Count(v => v == producerB) > 16;
            }, TimeSpan.FromSeconds(30), message: "Publisher B should claim A's orphaned partitions");

            // Wait for all messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain");

            _output.WriteLine("All messages drained after publisher B claimed orphaned partitions");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }
}
```

- [ ] **Step 2: Write GracefulShutdownTests**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class GracefulShutdownTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public GracefulShutdownTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task GracefulShutdown_ReleasesLeases_NewPublisherPicksUpImmediately()
    {
        var topic = OutboxTestHelper.UniqueTopic("graceful");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Use long lease so we can tell the difference between "released" and "expired"
        var (hostA, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o => o.LeaseDurationSeconds = 120);

        await hostA.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3)); // Let A register and claim partitions

        // Insert messages
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 30, topic, "key-1");
        await Task.Delay(TimeSpan.FromSeconds(2)); // Let A begin processing

        // Graceful shutdown
        await hostA.StopAsync();
        hostA.Dispose();
        _output.WriteLine("Host A stopped gracefully");

        // Assert: producer unregistered
        var producers = await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString);
        Assert.Empty(producers);

        // Assert: leased messages have leased_until_utc = NULL (released, not waiting for 120s expiry)
        var remaining = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
        if (remaining > 0)
        {
            await using var conn = new Npgsql.NpgsqlConnection(_infra.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT COUNT(*) FROM outbox WHERE leased_until_utc IS NOT NULL", conn);
            var leased = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(0, leased); // All leases should be released
            _output.WriteLine($"{remaining} messages remain but all leases released");
        }

        // Start publisher B — should pick up remaining messages immediately (not waiting 120s)
        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        var startTime = DateTime.UtcNow;

        try
        {
            await hostB.StartAsync();

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(20), message: "B should process remaining messages quickly");

            var elapsed = DateTime.UtcNow - startTime;
            _output.WriteLine($"Publisher B drained remaining messages in {elapsed.TotalSeconds:F1}s");

            // Should be much less than the 120s lease duration
            Assert.True(elapsed.TotalSeconds < 30,
                $"Messages should be picked up within seconds, not waiting for 120s lease expiry. Took {elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Outbox.IntegrationTests --filter "ProcessKillTests|GracefulShutdownTests" -v n`
Expected: 2 tests passed

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/ProcessKillTests.cs tests/Outbox.IntegrationTests/Scenarios/GracefulShutdownTests.cs
git commit -m "test: add Scenario 3 (process kill) and Scenario 4 (graceful shutdown) integration tests"
```

---

## Chunk 3: Message Handling Scenarios (5-7)

### Task 8: Scenario 5 — Poison Message

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/PoisonMessageTests.cs`

- [ ] **Step 1: Write the test class**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class PoisonMessageTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public PoisonMessageTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PoisonMessage_EventuallyDeadLettered_DoesNotBlockOtherMessages()
    {
        var topic = OutboxTestHelper.UniqueTopic("poison");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Use MaxRetryCount=3 for faster test
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o => o.MaxRetryCount = 3);

        // Make transport fail for a specific partition key (simulating oversized message)
        var poisonKey = "poison-key";
        transport.SetIntermittentPredicate(msg => msg.PartitionKey == poisonKey);

        try
        {
            // Insert poison message + normal messages
            await InsertPoisonMessageAsync(_infra.ConnectionString, topic, poisonKey);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, topic, "healthy-key");

            await host.StartAsync();

            // Wait for processing
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var outboxCount = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
                var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);
                return outboxCount == 0 && dlqCount >= 1;
            }, TimeSpan.FromSeconds(30), message: "Poison message should be dead-lettered, healthy messages published");

            // Assert: dead letter has the poison message
            var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);
            Assert.True(dlqCount >= 1, $"Expected at least 1 dead-lettered message, got {dlqCount}");

            // Assert: healthy messages were published
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 5, TimeSpan.FromSeconds(5));
            Assert.True(consumed.Count >= 5, $"Expected at least 5 healthy messages, got {consumed.Count}");

            // Assert: outbox is empty
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));

            _output.WriteLine($"Poison message dead-lettered. DLQ count: {dlqCount}. Consumed: {consumed.Count}");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task InsertPoisonMessageAsync(string connectionString, string topic, string partitionKey)
    {
        await using var conn = new Npgsql.NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO outbox (topic_name, partition_key, event_type, payload, event_datetime_utc, event_ordinal)
            VALUES (@topic, @key, 'PoisonEvent', '{""poison"":true}', clock_timestamp(), 0)", conn);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@key", partitionKey);
        await cmd.ExecuteNonQueryAsync();
    }
}
```

**Note:** This test requires adding a `SetIntermittentPredicate` method to `FaultyTransportWrapper`. Update the wrapper:

- [ ] **Step 2: Add predicate-based failure to FaultyTransportWrapper**

Add to `FaultyTransportWrapper.cs`:

```csharp
private volatile Func<OutboxMessage, bool>? _failPredicate;

/// <summary>Fail sends where any message matches the predicate.</summary>
public void SetIntermittentPredicate(Func<OutboxMessage, bool> predicate) =>
    _failPredicate = predicate;
```

Add this check at the top of `SendAsync`, after the `_failing` check and before the `_failEveryN` check:

```csharp
var pred = _failPredicate;
if (pred != null && messages.Any(pred))
    throw new InvalidOperationException("Simulated failure for matching message");
```

Add to `Reset()`:

```csharp
_failPredicate = null;
```

- [ ] **Step 3: Run test**

Run: `dotnet test tests/Outbox.IntegrationTests --filter PoisonMessageTests -v n`
Expected: 1 test passed

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/PoisonMessageTests.cs tests/Outbox.IntegrationTests/Helpers/FaultyTransportWrapper.cs
git commit -m "test: add Scenario 5 (poison message) integration test"
```

---

### Task 9: Scenario 6 — Intermittent Failures

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/IntermittentFailureTests.cs`

- [ ] **Step 1: Write the test class**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class IntermittentFailureTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public IntermittentFailureTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task IntermittentFailures_RetryCountIncrements_MessagesEventuallyPublishedOrDeadLettered()
    {
        var topic = OutboxTestHelper.UniqueTopic("intermittent");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // MaxRetryCount=5, high circuit threshold so circuit doesn't open
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.MaxRetryCount = 5;
                o.CircuitBreakerFailureThreshold = 100; // Don't open circuit
            });

        // Fail 2 out of 3 calls (succeed every 3rd)
        transport.SetIntermittent(3);

        try
        {
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");
            await host.StartAsync();

            // Wait for all messages to be either published or dead-lettered
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All messages should be processed (published or dead-lettered)");

            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 1, TimeSpan.FromSeconds(5));
            var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);

            _output.WriteLine($"Consumed: {consumed.Count}, Dead-lettered: {dlqCount}");

            // All messages should be accounted for (published + dead-lettered)
            // Some may be duplicated in Kafka, so consumed.Count >= some number
            Assert.True(consumed.Count + dlqCount >= 10,
                $"All 10 messages should be either published ({consumed.Count}) or dead-lettered ({dlqCount})");

            // Key assertion: retry_count was incrementing (messages didn't retry forever)
            Assert.Equal(0, await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString));
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/Outbox.IntegrationTests --filter IntermittentFailureTests -v n`
Expected: 1 test passed

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/IntermittentFailureTests.cs
git commit -m "test: add Scenario 6 (intermittent failures) integration test"
```

---

### Task 10: Scenario 7 — Circuit Breaker Doesn't Burn Retries

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/CircuitBreakerRetryTests.cs`

- [ ] **Step 1: Write the test class**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class CircuitBreakerRetryTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public CircuitBreakerRetryTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task CircuitOpen_DoesNotIncrementRetryCount_MessagesPublishAfterRecovery()
    {
        var topic = OutboxTestHelper.UniqueTopic("circuit-retry");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.MaxRetryCount = 3;
                o.CircuitBreakerFailureThreshold = 2; // Open after 2 failures
                o.CircuitBreakerOpenDurationSeconds = 5;
            });

        try
        {
            await host.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(2)); // Let publisher register

            // Block broker
            transport.SetFailing(true);
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");

            // Wait for circuit to open (2 failures → circuit opens)
            await Task.Delay(TimeSpan.FromSeconds(5));

            // Check retry counts — should be ≤ 2 (from the 2 failures before circuit opened)
            var retryCounts = await OutboxTestHelper.GetRetryCountsAsync(_infra.ConnectionString);
            foreach (var (seq, retryCount) in retryCounts)
            {
                Assert.True(retryCount <= 2,
                    $"Message {seq} has retry_count={retryCount}, expected ≤ 2 (circuit should prevent further increments)");
            }
            _output.WriteLine($"Retry counts during circuit open: {string.Join(", ", retryCounts.Select(r => r.RetryCount))}");

            // Wait through a few more circuit cycles with broker still down
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Re-check: retry counts should NOT have reached MaxRetryCount
            retryCounts = await OutboxTestHelper.GetRetryCountsAsync(_infra.ConnectionString);
            foreach (var (seq, retryCount) in retryCounts)
            {
                Assert.True(retryCount < 3,
                    $"Message {seq} has retry_count={retryCount}, should be < MaxRetryCount (3). Circuit open should NOT burn retries.");
            }

            // No messages should be dead-lettered
            Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));

            // Restore broker
            transport.SetFailing(false);

            // Wait for messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should publish after broker recovery");

            // Assert: all messages published, none dead-lettered
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 10, TimeSpan.FromSeconds(10));
            Assert.True(consumed.Count >= 10, $"Expected at least 10 messages, got {consumed.Count}");
            Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));

            _output.WriteLine("All messages published after recovery, none dead-lettered");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/Outbox.IntegrationTests --filter CircuitBreakerRetryTests -v n`
Expected: 1 test passed

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/CircuitBreakerRetryTests.cs
git commit -m "test: add Scenario 7 (circuit breaker doesn't burn retries) integration test"
```

---

## Chunk 4: Operational Scenarios (8-13)

### Task 11: Scenario 8 — Multi-Publisher Rebalance

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/MultiPublisherRebalanceTests.cs`

- [ ] **Step 1: Write the test class**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class MultiPublisherRebalanceTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public MultiPublisherRebalanceTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task ScaleUp_PartitionsRedistributedFairly()
    {
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);
        var topic = OutboxTestHelper.UniqueTopic("rebalance");

        // Start publisher A — should own all 32 partitions
        var (hostA, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
            return owners.Values.Count(v => v != null) == 32;
        }, TimeSpan.FromSeconds(15), message: "A should own all 32 partitions");

        var producers = await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString);
        _output.WriteLine($"A owns 32 partitions. ProducerId: {producers[0]}");

        // Start publisher B
        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostB.StartAsync();

        // Wait for rebalance — should be roughly 16/16
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
            var grouped = owners.Values.Where(v => v != null).GroupBy(v => v).ToList();
            return grouped.Count == 2 && grouped.All(g => g.Count() >= 12); // ~16 each, allowing some variance
        }, TimeSpan.FromSeconds(30), message: "Partitions should be split roughly 16/16");

        var ownersAfter = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
        var distribution = ownersAfter.Values.Where(v => v != null).GroupBy(v => v)
            .Select(g => $"{g.Key}: {g.Count()} partitions").ToList();
        _output.WriteLine($"After rebalance: {string.Join(", ", distribution)}");

        // Insert messages — both should process
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic);
        await OutboxTestHelper.WaitUntilAsync(
            () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
            TimeSpan.FromSeconds(20), message: "All messages should drain with 2 publishers");

        // Scale down: stop B
        await hostB.StopAsync();
        hostB.Dispose();

        // A should reclaim all partitions
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
            return owners.Values.Count(v => v != null) == 32
                && owners.Values.Distinct().Count(v => v != null) == 1;
        }, TimeSpan.FromSeconds(30), message: "A should reclaim all 32 partitions after B stops");

        _output.WriteLine("A reclaimed all partitions after B stopped");

        await hostA.StopAsync();
        hostA.Dispose();
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/Outbox.IntegrationTests --filter MultiPublisherRebalanceTests -v n`
Expected: 1 test passed

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/MultiPublisherRebalanceTests.cs
git commit -m "test: add Scenario 8 (multi-publisher rebalance) integration test"
```

---

### Task 12: Scenario 9 — Loop Crash Recovery

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/LoopCrashRecoveryTests.cs`

- [ ] **Step 1: Write the test class**

```csharp
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Outbox.Core.Observability;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class LoopCrashRecoveryTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public LoopCrashRecoveryTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task TransientLoopCrash_AutoRestarts_AndContinuesProcessing()
    {
        var topic = OutboxTestHelper.UniqueTopic("loop-crash");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var connFactory = new ToggleableConnectionFactory(_infra.ConnectionString);
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            connectionFactory: connFactory);

        var healthState = host.Services.GetService(typeof(OutboxHealthState)) as OutboxHealthState;

        try
        {
            await host.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Verify healthy operation
            Assert.True(healthState!.IsPublishLoopRunning);

            // Inject transient DB failure (causes all loops to error)
            connFactory.SetFailing(true);
            await Task.Delay(TimeSpan.FromSeconds(5));

            // Should have restarted at least once
            Assert.True(healthState.ConsecutiveLoopRestarts > 0,
                "Expected at least 1 loop restart after transient DB failure");
            _output.WriteLine($"Loop restarts: {healthState.ConsecutiveLoopRestarts}");

            // Restore DB
            connFactory.SetFailing(false);

            // Insert messages — should be processed after recovery
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 10, topic, "key-1");

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(40), message: "Messages should drain after loop recovery");

            // After sustained healthy operation, restart counter should reset
            await OutboxTestHelper.WaitUntilAsync(
                () => Task.FromResult(healthState.ConsecutiveLoopRestarts == 0),
                TimeSpan.FromSeconds(40), message: "Restart counter should reset after sustained healthy operation");

            _output.WriteLine("Loops recovered, messages processed, restart counter reset");
        }
        finally
        {
            connFactory.SetFailing(false);
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/Outbox.IntegrationTests --filter LoopCrashRecoveryTests -v n`
Expected: 1 test passed

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/LoopCrashRecoveryTests.cs
git commit -m "test: add Scenario 9 (loop crash recovery) integration test"
```

---

### Task 13: Scenario 10 — Health Check + Scenario 11 — Pending Metric

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/HealthCheckTests.cs`
- Create: `tests/Outbox.IntegrationTests/Scenarios/PendingMetricTests.cs`

- [ ] **Step 1: Write HealthCheckTests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class HealthCheckTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public HealthCheckTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task HealthCheck_ReportsCorrectStates()
    {
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        try
        {
            await host.StartAsync();

            // Phase 1: Healthy
            await Task.Delay(TimeSpan.FromSeconds(5));
            var report = await healthCheckService.CheckHealthAsync();
            var outboxEntry = report.Entries["outbox"];

            _output.WriteLine($"Phase 1 - Status: {outboxEntry.Status}, Description: {outboxEntry.Description}");
            Assert.Equal(HealthStatus.Healthy, outboxEntry.Status);

            // Phase 2: Degraded (open circuit breaker)
            transport.SetFailing(true);
            var topic = OutboxTestHelper.UniqueTopic("health");
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, topic, "key-1");
            await Task.Delay(TimeSpan.FromSeconds(8)); // Wait for circuit to open

            report = await healthCheckService.CheckHealthAsync();
            outboxEntry = report.Entries["outbox"];
            _output.WriteLine($"Phase 2 - Status: {outboxEntry.Status}, Description: {outboxEntry.Description}");
            Assert.Equal(HealthStatus.Degraded, outboxEntry.Status);

            // Phase 3: Recover
            transport.SetFailing(false);
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var r = await healthCheckService.CheckHealthAsync();
                return r.Entries["outbox"].Status == HealthStatus.Healthy;
            }, TimeSpan.FromSeconds(30), message: "Health should return to Healthy");

            _output.WriteLine("Phase 3 - Recovered to Healthy");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 2: Write PendingMetricTests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Observability;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class PendingMetricTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public PendingMetricTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PendingGauge_ReflectsOutboxDepth()
    {
        var topic = OutboxTestHelper.UniqueTopic("pending");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Block transport so messages accumulate
        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        transport.SetFailing(true);

        try
        {
            await host.StartAsync();

            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic, "key-1");

            // Wait for at least one heartbeat cycle to update pending count
            await Task.Delay(TimeSpan.FromSeconds(5));

            var pending = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
            _output.WriteLine($"Pending messages in DB: {pending}");
            Assert.True(pending > 0, "Messages should be pending");

            // Restore transport
            transport.SetFailing(false);

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain");

            _output.WriteLine("All messages drained, pending = 0");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Outbox.IntegrationTests --filter "HealthCheckTests|PendingMetricTests" -v n`
Expected: 2 tests passed

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/HealthCheckTests.cs tests/Outbox.IntegrationTests/Scenarios/PendingMetricTests.cs
git commit -m "test: add Scenario 10 (health check) and Scenario 11 (pending metric) integration tests"
```

---

### Task 14: Scenario 12 — Dead Letter Replay + Scenario 13 — Ordering

**Files:**
- Create: `tests/Outbox.IntegrationTests/Scenarios/DeadLetterReplayTests.cs`
- Create: `tests/Outbox.IntegrationTests/Scenarios/OrderingTests.cs`

- [ ] **Step 1: Write DeadLetterReplayTests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Abstractions;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class DeadLetterReplayTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public DeadLetterReplayTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task DeadLetterReplay_MovesBackToOutbox_AndPublishes()
    {
        var topic = OutboxTestHelper.UniqueTopic("replay");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Phase 1: Create dead-lettered messages by using MaxRetryCount=1 and failing transport
        var (host1, transport1) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o => o.MaxRetryCount = 1);
        transport1.SetFailing(true);

        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 3, topic, "key-1");
        await host1.StartAsync();

        // Wait for messages to be dead-lettered
        await OutboxTestHelper.WaitUntilAsync(
            () => OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result >= 3),
            TimeSpan.FromSeconds(30), message: "Messages should be dead-lettered");

        await host1.StopAsync();
        host1.Dispose();

        var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);
        _output.WriteLine($"Dead-lettered messages: {dlqCount}");
        Assert.True(dlqCount >= 3);

        // Phase 2: Replay dead-lettered messages
        var (host2, transport2) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        var dlManager = host2.Services.GetRequiredService<IDeadLetterManager>();

        // Get dead-lettered sequence numbers
        var deadLettered = await dlManager.GetAsync(100, 0, CancellationToken.None);
        Assert.True(deadLettered.Count >= 3);

        // Replay them
        await dlManager.ReplayAsync(
            deadLettered.Select(d => d.SequenceNumber).ToList(),
            CancellationToken.None);

        // Assert: moved from dead letter back to outbox
        Assert.Equal(0, await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString));
        Assert.True(await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString) >= 3);

        // Phase 3: Start publisher — should publish the replayed messages
        try
        {
            await host2.StartAsync();

            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(20), message: "Replayed messages should be published");

            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 3, TimeSpan.FromSeconds(5));
            Assert.True(consumed.Count >= 3, $"Expected at least 3 replayed messages, got {consumed.Count}");

            _output.WriteLine($"Replayed messages published: {consumed.Count}");
        }
        finally
        {
            await host2.StopAsync();
            host2.Dispose();
        }
    }
}
```

- [ ] **Step 2: Write OrderingTests**

```csharp
using System.Text.Json;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class OrderingTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public OrderingTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task PerPartitionKeyOrdering_PreservedThroughFailures()
    {
        var topic = OutboxTestHelper.UniqueTopic("ordering");
        var partitionKey = "order-123";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (host, transport) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                o.CircuitBreakerFailureThreshold = 100; // Don't open circuit
                o.MaxRetryCount = 10; // High so messages don't dead-letter
            });

        // Intermittent failures: succeed every 3rd call
        transport.SetIntermittent(3);

        try
        {
            // Insert 50 ordered messages with sequential ordinals
            await OutboxTestHelper.InsertMessagesAsync(
                _infra.ConnectionString, 50, topic, partitionKey, startOrdinal: 0);

            await host.StartAsync();

            // Wait for all to publish
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All ordered messages should drain");

            // Consume and verify order
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 50, TimeSpan.FromSeconds(10));

            // Extract index from payload {"index":N}
            var indices = consumed
                .Select(m =>
                {
                    var doc = JsonDocument.Parse(m.Value);
                    return doc.RootElement.GetProperty("index").GetInt32();
                })
                .ToList();

            _output.WriteLine($"Consumed {consumed.Count} messages. Indices (first 20): {string.Join(", ", indices.Take(20))}");

            // Deduplicate (at-least-once may produce duplicates)
            var deduped = new List<int>();
            var seen = new HashSet<int>();
            foreach (var idx in indices)
            {
                if (seen.Add(idx))
                    deduped.Add(idx);
            }

            // Assert: all 50 messages present
            Assert.Equal(50, deduped.Count);

            // Assert: ordering preserved (each element > previous)
            for (int i = 1; i < deduped.Count; i++)
            {
                Assert.True(deduped[i] > deduped[i - 1],
                    $"Ordering violation: index {deduped[i]} came after {deduped[i - 1]} at position {i}");
            }

            _output.WriteLine("Ordering verified: all 50 messages in order, no gaps");
        }
        finally
        {
            transport.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Outbox.IntegrationTests --filter "DeadLetterReplayTests|OrderingTests" -v n`
Expected: 2 tests passed

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.IntegrationTests/Scenarios/DeadLetterReplayTests.cs tests/Outbox.IntegrationTests/Scenarios/OrderingTests.cs
git commit -m "test: add Scenario 12 (dead letter replay) and Scenario 13 (ordering) integration tests"
```

---

### Task 15: Delete the smoke test + final verification

**Files:**
- Delete: `tests/Outbox.IntegrationTests/Scenarios/SmokeTest.cs` (was scaffolding, no longer needed)

- [ ] **Step 1: Delete the smoke test**

Delete `tests/Outbox.IntegrationTests/Scenarios/SmokeTest.cs`

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test tests/Outbox.IntegrationTests -v n --logger "console;verbosity=normal"`
Expected: All tests pass (approximately 12 tests across all scenario files)

- [ ] **Step 3: Run both test projects together**

Run: `dotnet test src/Outbox.slnx -v n`
Expected: All unit tests (18) + all integration tests pass

- [ ] **Step 4: Final commit**

```bash
git add -A tests/Outbox.IntegrationTests/
git commit -m "test: complete integration test suite for all 14 failure scenarios"
```
