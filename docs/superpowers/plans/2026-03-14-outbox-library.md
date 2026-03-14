# Outbox Library Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a pluggable .NET 10 transactional outbox library distributed as 5 NuGet packages (Core, SqlServer, PostgreSQL, Kafka, EventHub).

**Architecture:** Builder pattern entry point (`services.AddOutbox(...)`) with pluggable `IOutboxStore` (database) and `IOutboxTransport` (broker) abstractions. A `BackgroundService` publisher engine orchestrates 5 concurrent loops (publish, heartbeat, rebalance, orphan sweep, dead-letter sweep). Each provider is a separate NuGet package with extension methods on `IOutboxBuilder`.

**Tech Stack:** .NET 10, Microsoft.Extensions.*, Microsoft.Data.SqlClient, Npgsql, Confluent.Kafka, Azure.Messaging.EventHubs, xUnit, NSubstitute, System.Diagnostics (ActivitySource, Meter)

**Spec:** `docs/superpowers/specs/2026-03-14-outbox-library-design.md`

---

## File Structure

```
src/
├── Outbox.Core/
│   ├── Outbox.Core.csproj
│   ├── Models/
│   │   ├── OutboxMessage.cs
│   │   ├── DeadLetteredMessage.cs
│   │   └── CircuitState.cs
│   ├── Abstractions/
│   │   ├── IOutboxTransport.cs
│   │   ├── IOutboxStore.cs
│   │   ├── IOutboxEventHandler.cs
│   │   └── IDeadLetterManager.cs
│   ├── Options/
│   │   └── OutboxPublisherOptions.cs
│   ├── Builder/
│   │   ├── IOutboxBuilder.cs
│   │   ├── OutboxBuilder.cs
│   │   ├── NoOpOutboxEventHandler.cs
│   │   └── OutboxServiceCollectionExtensions.cs
│   ├── Engine/
│   │   ├── OutboxPublisherService.cs
│   │   └── TopicCircuitBreaker.cs
│   └── Observability/
│       └── OutboxInstrumentation.cs
├── Outbox.SqlServer/
│   ├── Outbox.SqlServer.csproj
│   ├── SqlServerOutboxStore.cs
│   ├── SqlServerDeadLetterManager.cs
│   ├── SqlServerStoreOptions.cs
│   ├── SqlServerOutboxBuilderExtensions.cs
│   └── db_scripts/
│       └── install.sql
├── Outbox.PostgreSQL/
│   ├── Outbox.PostgreSQL.csproj
│   ├── PostgreSqlOutboxStore.cs
│   ├── PostgreSqlDeadLetterManager.cs
│   ├── PostgreSqlStoreOptions.cs
│   ├── PostgreSqlOutboxBuilderExtensions.cs
│   └── db_scripts/
│       └── install.sql
├── Outbox.Kafka/
│   ├── Outbox.Kafka.csproj
│   ├── KafkaOutboxTransport.cs
│   ├── KafkaTransportOptions.cs
│   └── KafkaOutboxBuilderExtensions.cs
└── Outbox.EventHub/
    ├── Outbox.EventHub.csproj
    ├── EventHubOutboxTransport.cs
    ├── EventHubTransportOptions.cs
    └── EventHubOutboxBuilderExtensions.cs
tests/
└── Outbox.Core.Tests/
    ├── Outbox.Core.Tests.csproj
    ├── TopicCircuitBreakerTests.cs
    ├── OutboxPublisherServiceTests.cs
    └── OutboxBuilderTests.cs
```

---

## Chunk 1: Solution Setup + Core Models & Interfaces

### Task 1: Create Solution and Core Project

**Files:**
- Create: `src/Outbox.sln`
- Create: `src/Outbox.Core/Outbox.Core.csproj`

- [ ] **Step 1: Create solution and project**

```bash
cd /home/vgmello/repos/poc/poc
mkdir -p src
cd src
dotnet new sln -n Outbox
dotnet new classlib -n Outbox.Core --framework net10.0 -o Outbox.Core
dotnet sln add Outbox.Core/Outbox.Core.csproj
```

- [ ] **Step 2: Configure Core csproj**

Edit `src/Outbox.Core/Outbox.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Outbox.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Build to verify**

```bash
cd /home/vgmello/repos/poc/poc/src
dotnet build Outbox.Core/Outbox.Core.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Create test project**

```bash
cd /home/vgmello/repos/poc/poc
dotnet new xunit -n Outbox.Core.Tests --framework net10.0 -o tests/Outbox.Core.Tests
cd src
dotnet sln add ../tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj
```

Add NSubstitute and project reference to `tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj`:

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
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Outbox.Core/Outbox.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Commit**

```bash
git add src/ tests/
git commit -m "feat: scaffold solution with Core project and test project"
```

---

### Task 2: Create Core Models

**Files:**
- Create: `src/Outbox.Core/Models/OutboxMessage.cs`
- Create: `src/Outbox.Core/Models/DeadLetteredMessage.cs`
- Create: `src/Outbox.Core/Models/CircuitState.cs`

- [ ] **Step 1: Create OutboxMessage**

Create `src/Outbox.Core/Models/OutboxMessage.cs`:

```csharp
namespace Outbox.Core.Models;

public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    DateTimeOffset EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount,
    DateTimeOffset CreatedAtUtc);
```

- [ ] **Step 2: Create DeadLetteredMessage**

Create `src/Outbox.Core/Models/DeadLetteredMessage.cs`:

```csharp
namespace Outbox.Core.Models;

public sealed record DeadLetteredMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    string? Headers,
    string Payload,
    DateTimeOffset EventDateTimeUtc,
    short EventOrdinal,
    int RetryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadLetteredAtUtc,
    string? LastError);
```

- [ ] **Step 3: Create CircuitState**

Create `src/Outbox.Core/Models/CircuitState.cs`:

```csharp
namespace Outbox.Core.Models;

public enum CircuitState { Closed, Open, HalfOpen }
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build src/Outbox.Core/Outbox.Core.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.Core/Models/
git commit -m "feat: add core models (OutboxMessage, DeadLetteredMessage, CircuitState)"
```

---

### Task 3: Create Core Abstractions

**Files:**
- Create: `src/Outbox.Core/Abstractions/IOutboxTransport.cs`
- Create: `src/Outbox.Core/Abstractions/IOutboxStore.cs`
- Create: `src/Outbox.Core/Abstractions/IOutboxEventHandler.cs`
- Create: `src/Outbox.Core/Abstractions/IDeadLetterManager.cs`

- [ ] **Step 1: Create IOutboxTransport**

Create `src/Outbox.Core/Abstractions/IOutboxTransport.cs`:

```csharp
using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxTransport : IAsyncDisposable
{
    Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Create IOutboxStore**

Create `src/Outbox.Core/Abstractions/IOutboxStore.cs`:

```csharp
using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxStore
{
    Task<string> RegisterProducerAsync(CancellationToken ct);
    Task UnregisterProducerAsync(string producerId, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> LeaseBatchAsync(
        string producerId, int batchSize, int leaseDurationSeconds,
        int maxRetryCount, CancellationToken ct);

    Task DeletePublishedAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task ReleaseLeaseAsync(
        string producerId, IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers, string? lastError, CancellationToken ct);

    Task HeartbeatAsync(string producerId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string producerId, CancellationToken ct);
    Task RebalanceAsync(string producerId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string producerId, CancellationToken ct);

    Task SweepDeadLettersAsync(int maxRetryCount, CancellationToken ct);
}
```

- [ ] **Step 3: Create IOutboxEventHandler**

Create `src/Outbox.Core/Abstractions/IOutboxEventHandler.cs`:

```csharp
using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxEventHandler
{
    Task OnMessagePublishedAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnMessageDeadLetteredAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnPublishFailedAsync(
        IReadOnlyList<OutboxMessage> messages, Exception exception, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnCircuitBreakerStateChangedAsync(
        string topicName, CircuitState state, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnRebalanceAsync(
        string producerId, IReadOnlyList<int> ownedPartitions, CancellationToken ct) =>
        Task.CompletedTask;
}
```

- [ ] **Step 4: Create IDeadLetterManager**

Create `src/Outbox.Core/Abstractions/IDeadLetterManager.cs`:

```csharp
using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IDeadLetterManager
{
    Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct);

    Task ReplayAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task PurgeAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    Task PurgeAllAsync(CancellationToken ct);
}
```

- [ ] **Step 5: Build to verify**

```bash
dotnet build src/Outbox.Core/Outbox.Core.csproj
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/Outbox.Core/Abstractions/
git commit -m "feat: add core abstractions (IOutboxTransport, IOutboxStore, IOutboxEventHandler, IDeadLetterManager)"
```

---

## Chunk 2: Core Options, Builder & Observability

### Task 4: Create OutboxPublisherOptions

**Files:**
- Create: `src/Outbox.Core/Options/OutboxPublisherOptions.cs`

- [ ] **Step 1: Create the options class**

Create `src/Outbox.Core/Options/OutboxPublisherOptions.cs`:

```csharp
namespace Outbox.Core.Options;

public sealed class OutboxPublisherOptions
{
    public int BatchSize { get; set; } = 100;
    public int LeaseDurationSeconds { get; set; } = 45;
    public int MaxRetryCount { get; set; } = 5;
    public int MinPollIntervalMs { get; set; } = 100;
    public int MaxPollIntervalMs { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 10_000;
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int PartitionGracePeriodSeconds { get; set; } = 60;
    public int RebalanceIntervalMs { get; set; } = 30_000;
    public int OrphanSweepIntervalMs { get; set; } = 60_000;
    public int DeadLetterSweepIntervalMs { get; set; } = 60_000;
    public int CircuitBreakerFailureThreshold { get; set; } = 3;
    public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/Outbox.Core/Outbox.Core.csproj
git add src/Outbox.Core/Options/
git commit -m "feat: add OutboxPublisherOptions"
```

---

### Task 5: Create Observability Instrumentation

**Files:**
- Create: `src/Outbox.Core/Observability/OutboxInstrumentation.cs`

- [ ] **Step 1: Create instrumentation class**

Create `src/Outbox.Core/Observability/OutboxInstrumentation.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Outbox.Core.Observability;

internal sealed class OutboxInstrumentation : IDisposable
{
    public const string ActivitySourceName = "Outbox";
    public const string MeterName = "Outbox";

    public ActivitySource ActivitySource { get; }
    public Meter Meter { get; }

    // Counters
    public Counter<long> MessagesPublished { get; }
    public Counter<long> MessagesDeadLettered { get; }
    public Counter<long> CircuitBreakerStateChanges { get; }
    public Counter<long> PublishFailures { get; }

    // Histograms
    public Histogram<double> PublishDuration { get; }
    public Histogram<double> PollDuration { get; }
    public Histogram<int> BatchSize { get; }

    public OutboxInstrumentation(IMeterFactory meterFactory)
    {
        ActivitySource = new ActivitySource(ActivitySourceName);
        Meter = meterFactory.Create(MeterName);

        MessagesPublished = Meter.CreateCounter<long>(
            "outbox.messages.published",
            description: "Number of messages successfully published");

        MessagesDeadLettered = Meter.CreateCounter<long>(
            "outbox.messages.dead_lettered",
            description: "Number of messages moved to dead letter table");

        CircuitBreakerStateChanges = Meter.CreateCounter<long>(
            "outbox.circuit_breaker.state_changes",
            description: "Number of circuit breaker state transitions");

        PublishFailures = Meter.CreateCounter<long>(
            "outbox.publish.failures",
            description: "Number of failed publish attempts");

        PublishDuration = Meter.CreateHistogram<double>(
            "outbox.publish.duration",
            unit: "ms",
            description: "Duration of publish operations in milliseconds");

        PollDuration = Meter.CreateHistogram<double>(
            "outbox.poll.duration",
            unit: "ms",
            description: "Duration of poll operations in milliseconds");

        BatchSize = Meter.CreateHistogram<int>(
            "outbox.poll.batch_size",
            description: "Number of messages leased per poll");
    }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
```

- [ ] **Step 2: Add System.Diagnostics.DiagnosticSource package reference**

Add to `src/Outbox.Core/Outbox.Core.csproj`:

```xml
<PackageReference Include="System.Diagnostics.DiagnosticSource" Version="10.0.0" />
```

- [ ] **Step 3: Build and commit**

```bash
dotnet build src/Outbox.Core/Outbox.Core.csproj
git add src/Outbox.Core/Observability/ src/Outbox.Core/Outbox.Core.csproj
git commit -m "feat: add observability instrumentation (ActivitySource, Meter, counters, histograms)"
```

---

### Task 6: Create Builder (IOutboxBuilder + OutboxBuilder + Extension Methods)

**Files:**
- Create: `src/Outbox.Core/Builder/IOutboxBuilder.cs`
- Create: `src/Outbox.Core/Builder/OutboxBuilder.cs`
- Create: `src/Outbox.Core/Builder/OutboxServiceCollectionExtensions.cs`
- Create: `tests/Outbox.Core.Tests/OutboxBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Outbox.Core.Tests/OutboxBuilderTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.Core.Options;

namespace Outbox.Core.Tests;

public class OutboxBuilderTests
{
    [Fact]
    public void AddOutbox_RegistersHostedService()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigurePublisher(o => o.BatchSize = 50);
        });

        // Register required dependencies to avoid startup validation failure
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();
        Assert.Contains(hostedServices, s => s.GetType().Name == "OutboxPublisherService");
    }

    [Fact]
    public void AddOutbox_BindsPublisherOptionsFromConfiguration()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:Publisher:BatchSize"] = "200",
                ["Outbox:Publisher:MaxRetryCount"] = "10",
            })
            .Build();

        services.AddOutbox(config, _ => { });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>();
        Assert.Equal(200, options.CurrentValue.BatchSize);
        Assert.Equal(10, options.CurrentValue.MaxRetryCount);
    }

    [Fact]
    public void ConfigurePublisher_OverridesConfigurationValues()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:Publisher:BatchSize"] = "200",
            })
            .Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigurePublisher(o => o.BatchSize = 500);
        });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>();
        Assert.Equal(500, options.CurrentValue.BatchSize);
    }

    [Fact]
    public void ConfigureEvents_RegistersCustomHandler()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigureEvents<TestEventHandler>();
        });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IOutboxEventHandler>();
        Assert.IsType<TestEventHandler>(handler);
    }

    [Fact]
    public void ConfigureEvents_WithFactory_RegistersHandler()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        var mockHandler = Substitute.For<IOutboxEventHandler>();

        services.AddOutbox(config, outbox =>
        {
            outbox.ConfigureEvents(sp => mockHandler);
        });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IOutboxEventHandler>();
        Assert.Same(mockHandler, handler);
    }

    [Fact]
    public void AddOutbox_WithoutEventHandler_RegistersNoOpHandler()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddOutbox(config, _ => { });
        services.AddSingleton(Substitute.For<IOutboxStore>());
        services.AddSingleton(Substitute.For<IOutboxTransport>());

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IOutboxEventHandler>();
        Assert.NotNull(handler);
    }

    private sealed class TestEventHandler : IOutboxEventHandler { }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd /home/vgmello/repos/poc/poc
dotnet test tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj
```

Expected: FAIL — types do not exist yet.

- [ ] **Step 3: Create IOutboxBuilder**

Create `src/Outbox.Core/Builder/IOutboxBuilder.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Abstractions;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
    IConfiguration Configuration { get; }

    IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure);
    IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler;
    IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory);
}
```

- [ ] **Step 4: Create NoOpOutboxEventHandler**

Create `src/Outbox.Core/Builder/NoOpOutboxEventHandler.cs`:

```csharp
using Outbox.Core.Abstractions;

namespace Outbox.Core.Builder;

internal sealed class NoOpOutboxEventHandler : IOutboxEventHandler { }
```

- [ ] **Step 5: Create OutboxBuilder**

Create `src/Outbox.Core/Builder/OutboxBuilder.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Outbox.Core.Abstractions;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

internal sealed class OutboxBuilder : IOutboxBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }

    public OutboxBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }

    public IOutboxBuilder ConfigurePublisher(Action<OutboxPublisherOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    public IOutboxBuilder ConfigureEvents<THandler>()
        where THandler : class, IOutboxEventHandler
    {
        Services.AddSingleton<IOutboxEventHandler, THandler>();
        return this;
    }

    public IOutboxBuilder ConfigureEvents(Func<IServiceProvider, IOutboxEventHandler> factory)
    {
        Services.AddSingleton(factory);
        return this;
    }
}
```

- [ ] **Step 6: Create OutboxServiceCollectionExtensions**

Create `src/Outbox.Core/Builder/OutboxServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Engine;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Builder;

public static class OutboxServiceCollectionExtensions
{
    public static IOutboxBuilder AddOutbox(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IOutboxBuilder> configure)
    {
        services.Configure<OutboxPublisherOptions>(
            configuration.GetSection("Outbox:Publisher"));

        services.AddSingleton<OutboxInstrumentation>();
        services.AddHostedService<OutboxPublisherService>();

        var builder = new OutboxBuilder(services, configuration);
        configure(builder);

        // Register no-op event handler if none was configured
        services.TryAddSingleton<IOutboxEventHandler, NoOpOutboxEventHandler>();

        return builder;
    }
}
```

- [ ] **Step 7: Create a stub OutboxPublisherService so it compiles**

Create `src/Outbox.Core/Engine/OutboxPublisherService.cs` (stub for now — full implementation in Chunk 3):

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Engine;

internal sealed class OutboxPublisherService : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxInstrumentation _instrumentation;

    public OutboxPublisherService(
        IOutboxStore store,
        IOutboxTransport transport,
        IOutboxEventHandler eventHandler,
        IOptionsMonitor<OutboxPublisherOptions> options,
        ILogger<OutboxPublisherService> logger,
        OutboxInstrumentation instrumentation)
    {
        _store = store;
        _transport = transport;
        _eventHandler = eventHandler;
        _options = options;
        _logger = logger;
        _instrumentation = instrumentation;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stub — full implementation in Task 7
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

```bash
cd /home/vgmello/repos/poc/poc
dotnet test tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj --verbosity normal
```

Expected: All 6 tests PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Outbox.Core/Builder/ src/Outbox.Core/Engine/ tests/Outbox.Core.Tests/
git commit -m "feat: add builder API (IOutboxBuilder, OutboxBuilder, AddOutbox extension)"
```

---

## Chunk 3: Publisher Engine

### Task 7: Implement TopicCircuitBreaker

**Files:**
- Create: `src/Outbox.Core/Engine/TopicCircuitBreaker.cs`
- Create: `tests/Outbox.Core.Tests/TopicCircuitBreakerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Outbox.Core.Tests/TopicCircuitBreakerTests.cs`:

```csharp
using Outbox.Core.Engine;
using Outbox.Core.Models;

namespace Outbox.Core.Tests;

public class TopicCircuitBreakerTests
{
    [Fact]
    public void IsOpen_NewCircuit_ReturnsFalse()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void RecordFailure_BelowThreshold_CircuitStaysClosed()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        var (opened, _) = cb.RecordFailure("orders");
        Assert.False(opened);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void RecordFailure_AtThreshold_CircuitOpens()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        cb.RecordFailure("orders");
        cb.RecordFailure("orders");
        var (opened, state) = cb.RecordFailure("orders");
        Assert.True(opened);
        Assert.Equal(CircuitState.Open, state);
        Assert.True(cb.IsOpen("orders"));
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);
        cb.RecordFailure("orders");
        cb.RecordFailure("orders");
        var (closed, state) = cb.RecordSuccess("orders");
        Assert.True(closed);
        Assert.Equal(CircuitState.Closed, state);
        Assert.False(cb.IsOpen("orders"));
    }

    [Fact]
    public void IsOpen_AfterOpenDurationExpires_ReturnsHalfOpen()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 1, openDurationSeconds: 0);
        cb.RecordFailure("orders");
        Assert.True(cb.IsOpen("orders"));

        // With 0 second duration, it should transition to half-open immediately
        Thread.Sleep(50);
        Assert.False(cb.IsOpen("orders"));
        Assert.Equal(CircuitState.HalfOpen, cb.GetState("orders"));
    }

    [Fact]
    public void DifferentTopics_HaveIndependentCircuits()
    {
        var cb = new TopicCircuitBreaker(failureThreshold: 1, openDurationSeconds: 30);
        cb.RecordFailure("orders");
        Assert.True(cb.IsOpen("orders"));
        Assert.False(cb.IsOpen("shipments"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj --filter "FullyQualifiedName~TopicCircuitBreaker"
```

Expected: FAIL — `TopicCircuitBreaker` does not exist.

- [ ] **Step 3: Implement TopicCircuitBreaker**

Create `src/Outbox.Core/Engine/TopicCircuitBreaker.cs`:

```csharp
using System.Collections.Concurrent;
using Outbox.Core.Models;

namespace Outbox.Core.Engine;

internal sealed class TopicCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly int _openDurationSeconds;
    private readonly ConcurrentDictionary<string, CircuitEntry> _circuits = new();

    public TopicCircuitBreaker(int failureThreshold, int openDurationSeconds)
    {
        _failureThreshold = failureThreshold;
        _openDurationSeconds = openDurationSeconds;
    }

    public bool IsOpen(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return false;

        lock (entry.Lock)
        {
            if (entry.State == CircuitState.Open &&
                DateTimeOffset.UtcNow >= entry.OpenedAtUtc.AddSeconds(_openDurationSeconds))
            {
                entry.State = CircuitState.HalfOpen;
                return false;
            }

            return entry.State == CircuitState.Open;
        }
    }

    public CircuitState GetState(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return CircuitState.Closed;

        lock (entry.Lock)
        {
            if (entry.State == CircuitState.Open &&
                DateTimeOffset.UtcNow >= entry.OpenedAtUtc.AddSeconds(_openDurationSeconds))
            {
                entry.State = CircuitState.HalfOpen;
            }

            return entry.State;
        }
    }

    /// <returns>(stateChanged, newState)</returns>
    public (bool StateChanged, CircuitState NewState) RecordFailure(string topicName)
    {
        var entry = _circuits.GetOrAdd(topicName, _ => new CircuitEntry());

        lock (entry.Lock)
        {
            entry.FailureCount++;
            if (entry.FailureCount >= _failureThreshold && entry.State != CircuitState.Open)
            {
                entry.State = CircuitState.Open;
                entry.OpenedAtUtc = DateTimeOffset.UtcNow;
                return (true, CircuitState.Open);
            }

            return (false, entry.State);
        }
    }

    /// <returns>(stateChanged, newState)</returns>
    public (bool StateChanged, CircuitState NewState) RecordSuccess(string topicName)
    {
        if (!_circuits.TryGetValue(topicName, out var entry))
            return (false, CircuitState.Closed);

        lock (entry.Lock)
        {
            var previousState = entry.State;
            entry.FailureCount = 0;
            entry.State = CircuitState.Closed;

            return (previousState != CircuitState.Closed, CircuitState.Closed);
        }
    }

    private sealed class CircuitEntry
    {
        public readonly object Lock = new();
        public int FailureCount;
        public CircuitState State = CircuitState.Closed;
        public DateTimeOffset OpenedAtUtc;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj --filter "FullyQualifiedName~TopicCircuitBreaker" --verbosity normal
```

Expected: All 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.Core/Engine/TopicCircuitBreaker.cs tests/Outbox.Core.Tests/TopicCircuitBreakerTests.cs
git commit -m "feat: add per-topic circuit breaker"
```

---

### Task 8: Implement OutboxPublisherService

**Files:**
- Modify: `src/Outbox.Core/Engine/OutboxPublisherService.cs`
- Create: `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Engine;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;
using System.Diagnostics.Metrics;

namespace Outbox.Core.Tests;

public class OutboxPublisherServiceTests
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly OutboxPublisherOptions _publisherOptions;
    private readonly OutboxPublisherService _sut;

    public OutboxPublisherServiceTests()
    {
        _store = Substitute.For<IOutboxStore>();
        _transport = Substitute.For<IOutboxTransport>();
        _eventHandler = Substitute.For<IOutboxEventHandler>();
        _publisherOptions = new OutboxPublisherOptions
        {
            BatchSize = 10,
            MinPollIntervalMs = 10,
            MaxPollIntervalMs = 50,
            HeartbeatIntervalMs = 100_000, // Large to avoid interference
            RebalanceIntervalMs = 100_000,
            OrphanSweepIntervalMs = 100_000,
            DeadLetterSweepIntervalMs = 100_000,
        };

        var optionsMonitor = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        optionsMonitor.CurrentValue.Returns(_publisherOptions);

        var meterFactory = new TestMeterFactory();
        var instrumentation = new OutboxInstrumentation(meterFactory);

        _store.RegisterProducerAsync(Arg.Any<CancellationToken>())
            .Returns("test-producer-1");

        _sut = new OutboxPublisherService(
            _store,
            _transport,
            _eventHandler,
            optionsMonitor,
            NullLogger<OutboxPublisherService>.Instance,
            instrumentation);
    }

    [Fact]
    public async Task RegistersAndUnregistersProducer()
    {
        _store.LeaseBatchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await _sut.StartAsync(cts.Token);
        await Task.Delay(100);
        await _sut.StopAsync(CancellationToken.None);

        await _store.Received(1).RegisterProducerAsync(Arg.Any<CancellationToken>());
        await _store.Received(1).UnregisterProducerAsync("test-producer-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_LeasesAndPublishesBatch()
    {
        var messages = new[]
        {
            CreateMessage(1, "orders", "customer-1"),
            CreateMessage(2, "orders", "customer-1"),
        };

        var callCount = 0;
        _store.LeaseBatchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<OutboxMessage>>(messages)
                    : Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await _sut.StartAsync(cts.Token);
        await Task.Delay(200);
        await _sut.StopAsync(CancellationToken.None);

        await _transport.Received(1).SendAsync(
            "orders", "customer-1",
            Arg.Is<IReadOnlyList<OutboxMessage>>(m => m.Count == 2),
            Arg.Any<CancellationToken>());

        await _store.Received(1).DeletePublishedAsync(
            "test-producer-1",
            Arg.Is<IReadOnlyList<long>>(ids => ids.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_OnTransportFailure_ReleasesLease()
    {
        var messages = new[] { CreateMessage(1, "orders", "customer-1") };

        var callCount = 0;
        _store.LeaseBatchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<OutboxMessage>>(messages)
                    : Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
            });

        _transport.SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Kafka down"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await _sut.StartAsync(cts.Token);
        await Task.Delay(200);
        await _sut.StopAsync(CancellationToken.None);

        await _store.Received(1).ReleaseLeaseAsync(
            "test-producer-1",
            Arg.Is<IReadOnlyList<long>>(ids => ids.Contains(1)),
            Arg.Any<CancellationToken>());

        await _store.DidNotReceive().DeletePublishedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_PoisonMessages_AreDeadLettered()
    {
        var poisonMessage = CreateMessage(1, "orders", "customer-1", retryCount: 5);
        var healthyMessage = CreateMessage(2, "orders", "customer-2", retryCount: 0);

        var callCount = 0;
        _store.LeaseBatchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<OutboxMessage>>(new[] { poisonMessage, healthyMessage })
                    : Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await _sut.StartAsync(cts.Token);
        await Task.Delay(200);
        await _sut.StopAsync(CancellationToken.None);

        await _store.Received(1).DeadLetterAsync(
            Arg.Is<IReadOnlyList<long>>(ids => ids.Contains(1) && ids.Count == 1),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _eventHandler.Received(1).OnMessageDeadLetteredAsync(
            poisonMessage, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishLoop_GroupsByTopicAndPartitionKey()
    {
        var messages = new[]
        {
            CreateMessage(1, "orders", "customer-1"),
            CreateMessage(2, "orders", "customer-2"),
            CreateMessage(3, "shipments", "customer-1"),
        };

        var callCount = 0;
        _store.LeaseBatchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<OutboxMessage>>(messages)
                    : Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await _sut.StartAsync(cts.Token);
        await Task.Delay(200);
        await _sut.StopAsync(CancellationToken.None);

        // Three groups: (orders, customer-1), (orders, customer-2), (shipments, customer-1)
        await _transport.Received(3).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    private static OutboxMessage CreateMessage(
        long seq, string topic, string partitionKey, int retryCount = 0)
    {
        return new OutboxMessage(
            SequenceNumber: seq,
            TopicName: topic,
            PartitionKey: partitionKey,
            EventType: "TestEvent",
            Headers: null,
            Payload: "{}",
            EventDateTimeUtc: DateTimeOffset.UtcNow,
            EventOrdinal: 0,
            RetryCount: retryCount,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }
        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj --filter "FullyQualifiedName~OutboxPublisherService"
```

Expected: FAIL — `OutboxPublisherService` is a stub.

- [ ] **Step 3: Implement OutboxPublisherService**

Replace `src/Outbox.Core/Engine/OutboxPublisherService.cs` with the full implementation. This is the core of the library — the 5-loop publisher engine. Port the logic from the POC (`OutboxPublisher.cs` and `OutboxPublisher.PostgreSQL.cs`) but delegate all DB/transport operations through the abstractions.

Key implementation points (refer to POC for detailed logic):
- `ExecuteAsync`: Register producer, `Task.WhenAll` the 5 loops, unregister on cancellation
- `PublishLoopAsync`: Lease → filter poison → group by (topic, key) → circuit breaker check → send → delete or release → adaptive polling
- `HeartbeatLoopAsync`: Call `_store.HeartbeatAsync` every `HeartbeatIntervalMs`
- `RebalanceLoopAsync`: Call `_store.RebalanceAsync` every `RebalanceIntervalMs`
- `OrphanSweepLoopAsync`: Call `_store.ClaimOrphanPartitionsAsync` every `OrphanSweepIntervalMs`
- `DeadLetterSweepLoopAsync`: Call `_store.SweepDeadLettersAsync` every `DeadLetterSweepIntervalMs`
- Use `_instrumentation` for all metrics/tracing
- Read `_options.CurrentValue` on each loop iteration (supports hot reload)
- Create `TopicCircuitBreaker` from current options values

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Engine;

internal sealed class OutboxPublisherService : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxInstrumentation _instrumentation;
    private string _producerId = string.Empty;
    private TopicCircuitBreaker _circuitBreaker = null!;

    public OutboxPublisherService(
        IOutboxStore store,
        IOutboxTransport transport,
        IOutboxEventHandler eventHandler,
        IOptionsMonitor<OutboxPublisherOptions> options,
        ILogger<OutboxPublisherService> logger,
        OutboxInstrumentation instrumentation)
    {
        _store = store;
        _transport = transport;
        _eventHandler = eventHandler;
        _options = options;
        _logger = logger;
        _instrumentation = instrumentation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup validation
        if (_store is null)
            throw new InvalidOperationException(
                "No IOutboxStore registered. Call .UseSqlServer() or .UsePostgreSql() in AddOutbox.");
        if (_transport is null)
            throw new InvalidOperationException(
                "No IOutboxTransport registered. Call .UseKafka() or .UseEventHub() in AddOutbox.");

        var opts = _options.CurrentValue;
        _circuitBreaker = new TopicCircuitBreaker(
            opts.CircuitBreakerFailureThreshold,
            opts.CircuitBreakerOpenDurationSeconds);

        _producerId = await _store.RegisterProducerAsync(stoppingToken);
        _logger.LogInformation("Outbox publisher registered as {ProducerId}", _producerId);

        try
        {
            await Task.WhenAll(
                PublishLoopAsync(stoppingToken),
                HeartbeatLoopAsync(stoppingToken),
                RebalanceLoopAsync(stoppingToken),
                OrphanSweepLoopAsync(stoppingToken),
                DeadLetterSweepLoopAsync(stoppingToken));
        }
        finally
        {
            try
            {
                await _store.UnregisterProducerAsync(_producerId, CancellationToken.None);
                _logger.LogInformation("Outbox publisher {ProducerId} unregistered", _producerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unregister producer {ProducerId}", _producerId);
            }
        }
    }

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        var pollIntervalMs = _options.CurrentValue.MinPollIntervalMs;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;
                var sw = Stopwatch.StartNew();

                using var activity = _instrumentation.ActivitySource.StartActivity("Outbox.PublishBatch");
                activity?.SetTag("outbox.producer_id", _producerId);

                var batch = await _store.LeaseBatchAsync(
                    _producerId, opts.BatchSize, opts.LeaseDurationSeconds,
                    opts.MaxRetryCount, ct);

                sw.Stop();
                _instrumentation.PollDuration.Record(sw.Elapsed.TotalMilliseconds);
                _instrumentation.BatchSize.Record(batch.Count);

                if (batch.Count == 0)
                {
                    pollIntervalMs = Math.Min(pollIntervalMs * 2, opts.MaxPollIntervalMs);
                    await Task.Delay(pollIntervalMs, ct);
                    continue;
                }

                pollIntervalMs = opts.MinPollIntervalMs;

                // Separate poison messages
                var poison = batch.Where(m => m.RetryCount >= opts.MaxRetryCount).ToList();
                var healthy = batch.Where(m => m.RetryCount < opts.MaxRetryCount).ToList();

                // Dead-letter poison messages
                if (poison.Count > 0)
                {
                    await _store.DeadLetterAsync(
                        poison.Select(m => m.SequenceNumber).ToList(),
                        "RetryCount exceeded MaxRetryCount", ct);

                    _instrumentation.MessagesDeadLettered.Add(poison.Count);

                    foreach (var msg in poison)
                    {
                        await _eventHandler.OnMessageDeadLetteredAsync(msg, ct);
                    }
                }

                // Group healthy messages by (TopicName, PartitionKey)
                var groups = healthy
                    .GroupBy(m => (m.TopicName, m.PartitionKey))
                    .ToList();

                foreach (var group in groups)
                {
                    var (topicName, partitionKey) = group.Key;
                    var messages = group.ToList();
                    var sequenceNumbers = messages.Select(m => m.SequenceNumber).ToList();

                    // Circuit breaker check
                    if (_circuitBreaker.IsOpen(topicName))
                    {
                        _logger.LogWarning(
                            "Circuit breaker open for topic {TopicName}, releasing {Count} messages",
                            topicName, messages.Count);
                        await _store.ReleaseLeaseAsync(_producerId, sequenceNumbers, ct);
                        continue;
                    }

                    try
                    {
                        var publishSw = Stopwatch.StartNew();
                        activity?.SetTag("outbox.topic", topicName);
                        activity?.SetTag("outbox.partition_key", partitionKey);
                        activity?.SetTag("outbox.batch_size", messages.Count);

                        await _transport.SendAsync(topicName, partitionKey, messages, ct);

                        publishSw.Stop();
                        _instrumentation.PublishDuration.Record(publishSw.Elapsed.TotalMilliseconds);

                        await _store.DeletePublishedAsync(_producerId, sequenceNumbers, ct);
                        _instrumentation.MessagesPublished.Add(messages.Count);

                        var (stateChanged, newState) = _circuitBreaker.RecordSuccess(topicName);
                        if (stateChanged)
                        {
                            _logger.LogInformation(
                                "Circuit breaker for topic {TopicName} transitioned to {State}",
                                topicName, newState);
                            _instrumentation.CircuitBreakerStateChanges.Add(1);
                            await _eventHandler.OnCircuitBreakerStateChangedAsync(
                                topicName, newState, ct);
                        }

                        foreach (var msg in messages)
                        {
                            await _eventHandler.OnMessagePublishedAsync(msg, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to publish {Count} messages to {TopicName}/{PartitionKey}",
                            messages.Count, topicName, partitionKey);

                        _instrumentation.PublishFailures.Add(1);
                        await _store.ReleaseLeaseAsync(_producerId, sequenceNumbers, ct);

                        await _eventHandler.OnPublishFailedAsync(messages, ex, ct);

                        var (stateChanged, newState) = _circuitBreaker.RecordFailure(topicName);
                        if (stateChanged)
                        {
                            _logger.LogWarning(
                                "Circuit breaker for topic {TopicName} transitioned to {State}",
                                topicName, newState);
                            _instrumentation.CircuitBreakerStateChanges.Add(1);
                            await _eventHandler.OnCircuitBreakerStateChangedAsync(
                                topicName, newState, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in publish loop");
                await Task.Delay(_options.CurrentValue.MinPollIntervalMs, ct);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.HeartbeatIntervalMs, ct);
                await _store.HeartbeatAsync(_producerId, ct);
                _logger.LogDebug("Heartbeat sent for {ProducerId}", _producerId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat failed for {ProducerId}", _producerId);
            }
        }
    }

    private async Task RebalanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.RebalanceIntervalMs, ct);
                await _store.RebalanceAsync(_producerId, ct);

                var ownedPartitions = await _store.GetOwnedPartitionsAsync(_producerId, ct);
                await _eventHandler.OnRebalanceAsync(_producerId, ownedPartitions, ct);

                _logger.LogDebug(
                    "Rebalance completed for {ProducerId}, owns {Count} partitions",
                    _producerId, ownedPartitions.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rebalance failed for {ProducerId}", _producerId);
            }
        }
    }

    private async Task OrphanSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.OrphanSweepIntervalMs, ct);
                await _store.ClaimOrphanPartitionsAsync(_producerId, ct);
                _logger.LogDebug("Orphan sweep completed for {ProducerId}", _producerId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orphan sweep failed for {ProducerId}", _producerId);
            }
        }
    }

    private async Task DeadLetterSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.DeadLetterSweepIntervalMs, ct);
                await _store.SweepDeadLettersAsync(_options.CurrentValue.MaxRetryCount, ct);
                _logger.LogDebug("Dead letter sweep completed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dead letter sweep failed");
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj --verbosity normal
```

Expected: All tests PASS (both builder tests and publisher service tests).

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.Core/Engine/ tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs
git commit -m "feat: implement OutboxPublisherService with 5 concurrent loops"
```

---

## Chunk 4: SQL Server Provider

### Task 9: Create SQL Server Project and Install Script

**Files:**
- Create: `src/Outbox.SqlServer/Outbox.SqlServer.csproj`
- Create: `src/Outbox.SqlServer/SqlServerStoreOptions.cs`
- Create: `src/Outbox.SqlServer/db_scripts/install.sql`

- [ ] **Step 1: Create project**

```bash
cd /home/vgmello/repos/poc/poc/src
dotnet new classlib -n Outbox.SqlServer --framework net10.0 -o Outbox.SqlServer
dotnet sln add Outbox.SqlServer/Outbox.SqlServer.csproj
```

Edit `src/Outbox.SqlServer/Outbox.SqlServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Outbox.SqlServer</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Outbox.Core/Outbox.Core.csproj" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.0.1" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="db_scripts/**" Pack="true" PackagePath="db_scripts" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create SqlServerStoreOptions**

Create `src/Outbox.SqlServer/SqlServerStoreOptions.cs`:

```csharp
namespace Outbox.SqlServer;

public sealed class SqlServerStoreOptions
{
    public int CommandTimeoutSeconds { get; set; } = 30;
    public string SchemaName { get; set; } = "dbo";
    public int TransientRetryMaxAttempts { get; set; } = 3;
    public int TransientRetryBackoffMs { get; set; } = 200;
}
```

- [ ] **Step 3: Create install.sql**

Port from the existing `EventHubOutbox.sql` in the POC root. Create `src/Outbox.SqlServer/db_scripts/install.sql`:

```sql
-- Outbox Library - SQL Server Schema
-- Run this script to create the required outbox tables.

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Outbox')
BEGIN
    CREATE TABLE dbo.Outbox (
        SequenceNumber      BIGINT IDENTITY(1,1) NOT NULL,
        TopicName           NVARCHAR(256)   NOT NULL,
        PartitionKey        NVARCHAR(256)   NOT NULL,
        EventType           NVARCHAR(256)   NOT NULL,
        Headers             NVARCHAR(MAX)   NULL,
        Payload             NVARCHAR(MAX)   NOT NULL,
        EventDateTimeUtc    DATETIME2(7)    NOT NULL,
        EventOrdinal        SMALLINT        NOT NULL DEFAULT 0,
        CreatedAtUtc        DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
        LeasedUntilUtc      DATETIME2(7)    NULL,
        LeaseOwner          NVARCHAR(256)   NULL,
        RetryCount          INT             NOT NULL DEFAULT 0,
        CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (SequenceNumber)
    );

    CREATE NONCLUSTERED INDEX IX_Outbox_Lease
        ON dbo.Outbox (LeasedUntilUtc, RetryCount)
        INCLUDE (PartitionKey, EventDateTimeUtc, EventOrdinal);
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OutboxDeadLetter')
BEGIN
    CREATE TABLE dbo.OutboxDeadLetter (
        SequenceNumber      BIGINT          NOT NULL,
        TopicName           NVARCHAR(256)   NOT NULL,
        PartitionKey        NVARCHAR(256)   NOT NULL,
        EventType           NVARCHAR(256)   NOT NULL,
        Headers             NVARCHAR(MAX)   NULL,
        Payload             NVARCHAR(MAX)   NOT NULL,
        EventDateTimeUtc    DATETIME2(7)    NOT NULL,
        EventOrdinal        SMALLINT        NOT NULL,
        CreatedAtUtc        DATETIME2(7)    NOT NULL,
        RetryCount          INT             NOT NULL,
        DeadLetteredAtUtc   DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
        LastError           NVARCHAR(MAX)   NULL,
        CONSTRAINT PK_OutboxDeadLetter PRIMARY KEY CLUSTERED (SequenceNumber)
    );
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OutboxProducers')
BEGIN
    CREATE TABLE dbo.OutboxProducers (
        ProducerId          NVARCHAR(256)   NOT NULL,
        RegisteredAtUtc     DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
        LastHeartbeatUtc    DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_OutboxProducers PRIMARY KEY CLUSTERED (ProducerId)
    );
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OutboxPartitions')
BEGIN
    CREATE TABLE dbo.OutboxPartitions (
        PartitionId         INT             NOT NULL,
        OwnerProducerId     NVARCHAR(256)   NULL,
        GraceExpiresUtc     DATETIME2(7)   NULL,
        CONSTRAINT PK_OutboxPartitions PRIMARY KEY CLUSTERED (PartitionId)
    );

    -- Seed default partitions (32)
    ;WITH Numbers AS (
        SELECT 0 AS N
        UNION ALL SELECT N + 1 FROM Numbers WHERE N < 31
    )
    INSERT INTO dbo.OutboxPartitions (PartitionId)
    SELECT N FROM Numbers;
END;

IF NOT EXISTS (SELECT * FROM sys.types WHERE name = 'SequenceNumberList')
BEGIN
    CREATE TYPE dbo.SequenceNumberList AS TABLE (
        SequenceNumber BIGINT NOT NULL
    );
END;
```

- [ ] **Step 4: Build and commit**

```bash
dotnet build src/Outbox.SqlServer/Outbox.SqlServer.csproj
git add src/Outbox.SqlServer/
git commit -m "feat: scaffold SQL Server provider with schema and options"
```

---

### Task 10: Implement SqlServerOutboxStore

**Files:**
- Create: `src/Outbox.SqlServer/SqlServerOutboxStore.cs`
- Create: `src/Outbox.SqlServer/SqlServerOutboxBuilderExtensions.cs`

- [ ] **Step 1: Implement SqlServerOutboxStore**

Create `src/Outbox.SqlServer/SqlServerOutboxStore.cs`. Port the query logic from the POC's `OutboxPublisher.cs` — all the SQL queries (`LeaseBatchAsync`, `DeletePublishedAsync`, `ReleaseLeaseAsync`, `DeadLetterAsync`, `HeartbeatAsync`, `RebalanceAsync`, `ClaimOrphanPartitionsAsync`, `SweepDeadLettersAsync`, `RegisterProducerAsync`, `UnregisterProducerAsync`).

Key implementation points:
- Accept `Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory` via constructor
- Each method opens a connection, executes, disposes
- Use `SqlServerStoreOptions` for `CommandTimeoutSeconds`, `SchemaName`
- Implement transient retry with `IsTransientSqlError` check and exponential backoff
- `LeaseBatchAsync`: Use the unified poll CTE with `ROWLOCK, READPAST`, conditional `RetryCount` increment, join against `OutboxPartitions`
- `DeletePublishedAsync`: Use TVP (`SequenceNumberList`) with `LeaseOwner` guard
- `ReleaseLeaseAsync`: Set `LeasedUntilUtc = NULL, LeaseOwner = NULL` (not just expire)
- `DeadLetterAsync`: Atomic `DELETE...OUTPUT INTO` with `LastError`
- `RebalanceAsync`: Fair-share calculation `CEILING(TotalPartitions / ActiveProducers)`, grace period handling

- [ ] **Step 2: Implement SqlServerOutboxBuilderExtensions**

Create `src/Outbox.SqlServer/SqlServerOutboxBuilderExtensions.cs`:

```csharp
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.SqlServer;

public static class SqlServerOutboxBuilderExtensions
{
    public static IOutboxBuilder UseSqlServer(
        this IOutboxBuilder builder,
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        Action<SqlServerStoreOptions>? configure = null)
    {
        builder.Services.Configure<SqlServerStoreOptions>(
            builder.Configuration.GetSection("Outbox:SqlServer"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.AddSingleton(connectionFactory);

        builder.Services.TryAddSingleton<IOutboxStore, SqlServerOutboxStore>();
        builder.Services.TryAddSingleton<IDeadLetterManager, SqlServerDeadLetterManager>();

        return builder;
    }
}
```

- [ ] **Step 3: Build and commit**

```bash
dotnet build src/Outbox.SqlServer/Outbox.SqlServer.csproj
git add src/Outbox.SqlServer/
git commit -m "feat: implement SQL Server outbox store and builder extensions"
```

---

### Task 11: Implement SqlServerDeadLetterManager

**Files:**
- Create: `src/Outbox.SqlServer/SqlServerDeadLetterManager.cs`

- [ ] **Step 1: Implement SqlServerDeadLetterManager**

Create `src/Outbox.SqlServer/SqlServerDeadLetterManager.cs`. Implement:
- `GetAsync`: `SELECT ... FROM OutboxDeadLetter ORDER BY SequenceNumber OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY`
- `ReplayAsync`: Atomic `DELETE FROM OutboxDeadLetter OUTPUT deleted.* INTO Outbox ... WHERE SequenceNumber IN (SELECT SequenceNumber FROM @ids)` with `RetryCount = 0`, `LeasedUntilUtc = NULL`, `LeaseOwner = NULL`
- `PurgeAsync`: `DELETE FROM OutboxDeadLetter WHERE SequenceNumber IN (SELECT SequenceNumber FROM @ids)`
- `PurgeAllAsync`: `DELETE FROM OutboxDeadLetter`

Uses the same connection factory and transient retry pattern as `SqlServerOutboxStore`.

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/Outbox.SqlServer/Outbox.SqlServer.csproj
git add src/Outbox.SqlServer/SqlServerDeadLetterManager.cs
git commit -m "feat: implement SQL Server dead letter manager"
```

---

## Chunk 5: PostgreSQL Provider

### Task 12: Create PostgreSQL Project, Schema, and Implementations

**Files:**
- Create: `src/Outbox.PostgreSQL/Outbox.PostgreSQL.csproj`
- Create: `src/Outbox.PostgreSQL/PostgreSqlStoreOptions.cs`
- Create: `src/Outbox.PostgreSQL/db_scripts/install.sql`
- Create: `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs`
- Create: `src/Outbox.PostgreSQL/PostgreSqlDeadLetterManager.cs`
- Create: `src/Outbox.PostgreSQL/PostgreSqlOutboxBuilderExtensions.cs`

Follow the same pattern as SQL Server (Task 9-11), but using PostgreSQL-specific SQL:
- `FOR UPDATE OF o SKIP LOCKED` instead of `ROWLOCK, READPAST`
- `hashtext()` instead of `CHECKSUM()`
- `ANY(@ids::bigint[])` instead of TVP
- CTE-based dead-lettering instead of `DELETE...OUTPUT INTO`
- `INSERT...ON CONFLICT...DO UPDATE` for producer registration
- `NOW()` and `make_interval(secs => ...)` for lease timestamps

Port queries from the POC's `OutboxPublisher.PostgreSQL.cs` and `EventHubOutbox.PostgreSQL.sql`.

- [ ] **Step 1: Create project**

```bash
cd /home/vgmello/repos/poc/poc/src
dotnet new classlib -n Outbox.PostgreSQL --framework net10.0 -o Outbox.PostgreSQL
dotnet sln add Outbox.PostgreSQL/Outbox.PostgreSQL.csproj
```

Edit csproj to reference Core + Npgsql.

- [ ] **Step 2: Create all files (options, schema, store, dead letter manager, builder extensions)**

Follow the same structure as SQL Server tasks, adapting all SQL to PostgreSQL syntax.

- [ ] **Step 3: Build and commit**

```bash
dotnet build src/Outbox.PostgreSQL/Outbox.PostgreSQL.csproj
git add src/Outbox.PostgreSQL/
git commit -m "feat: implement PostgreSQL provider (store, dead letter manager, schema)"
```

---

## Chunk 6: Kafka Transport

### Task 13: Create Kafka Transport Project and Implementation

**Files:**
- Create: `src/Outbox.Kafka/Outbox.Kafka.csproj`
- Create: `src/Outbox.Kafka/KafkaTransportOptions.cs`
- Create: `src/Outbox.Kafka/KafkaOutboxTransport.cs`
- Create: `src/Outbox.Kafka/KafkaOutboxBuilderExtensions.cs`

- [ ] **Step 1: Create project**

```bash
cd /home/vgmello/repos/poc/poc/src
dotnet new classlib -n Outbox.Kafka --framework net10.0 -o Outbox.Kafka
dotnet sln add Outbox.Kafka/Outbox.Kafka.csproj
```

Edit csproj to reference Core + `Confluent.Kafka` v2.8.0.

- [ ] **Step 2: Create KafkaTransportOptions**

Create `src/Outbox.Kafka/KafkaTransportOptions.cs`:

```csharp
namespace Outbox.Kafka;

public sealed class KafkaTransportOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string Acks { get; set; } = "All";
    public bool EnableIdempotence { get; set; } = true;
    public int MessageSendMaxRetries { get; set; } = 3;
    public int RetryBackoffMs { get; set; } = 500;
    public int LingerMs { get; set; } = 5;
    public int MessageTimeoutMs { get; set; } = 15_000;
    public int SendTimeoutSeconds { get; set; } = 15;
}
```

- [ ] **Step 3: Implement KafkaOutboxTransport**

Create `src/Outbox.Kafka/KafkaOutboxTransport.cs`:

```csharp
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.Kafka;

internal sealed class KafkaOutboxTransport : IOutboxTransport
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaOutboxTransport> _logger;
    private readonly int _sendTimeoutMs;

    public KafkaOutboxTransport(
        IProducer<string, string> producer,
        IOptions<KafkaTransportOptions> options,
        ILogger<KafkaOutboxTransport> logger)
    {
        _producer = producer;
        _logger = logger;
        _sendTimeoutMs = options.Value.SendTimeoutSeconds * 1000;
    }

    public async Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var msg in messages)
        {
            var kafkaMessage = new Message<string, string>
            {
                Key = partitionKey,
                Value = msg.Payload,
                Headers = ParseHeaders(msg.Headers),
            };

            using var timeoutCts = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_sendTimeoutMs);

            await _producer.ProduceAsync(topicName, kafkaMessage, timeoutCts.Token);
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }

    private static Headers? ParseHeaders(string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson))
            return null;

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        if (dict is null || dict.Count == 0)
            return null;

        var headers = new Headers();
        foreach (var kvp in dict)
        {
            headers.Add(kvp.Key, System.Text.Encoding.UTF8.GetBytes(kvp.Value));
        }
        return headers;
    }
}
```

- [ ] **Step 4: Implement KafkaOutboxBuilderExtensions**

Create `src/Outbox.Kafka/KafkaOutboxBuilderExtensions.cs`:

```csharp
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Builder;

namespace Outbox.Kafka;

public static class KafkaOutboxBuilderExtensions
{
    public static IOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Action<KafkaTransportOptions>? configure = null)
    {
        builder.Services.Configure<KafkaTransportOptions>(
            builder.Configuration.GetSection("Outbox:Kafka"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton<IProducer<string, string>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<KafkaTransportOptions>>().Value;
            var config = new ProducerConfig
            {
                BootstrapServers = opts.BootstrapServers,
                Acks = Enum.Parse<Acks>(opts.Acks, ignoreCase: true),
                EnableIdempotence = opts.EnableIdempotence,
                MessageSendMaxRetries = opts.MessageSendMaxRetries,
                RetryBackoffMs = opts.RetryBackoffMs,
                LingerMs = opts.LingerMs,
                MessageTimeoutMs = opts.MessageTimeoutMs,
            };
            return new ProducerBuilder<string, string>(config).Build();
        });

        builder.Services.TryAddSingleton<IOutboxTransport, KafkaOutboxTransport>();

        return builder;
    }

    public static IOutboxBuilder UseKafka(
        this IOutboxBuilder builder,
        Func<IServiceProvider, IProducer<string, string>> producerFactory,
        Action<KafkaTransportOptions>? configure = null)
    {
        builder.Services.Configure<KafkaTransportOptions>(
            builder.Configuration.GetSection("Outbox:Kafka"));

        if (configure is not null)
            builder.Services.Configure(configure);

        builder.Services.TryAddSingleton(producerFactory);
        builder.Services.TryAddSingleton<IOutboxTransport, KafkaOutboxTransport>();

        return builder;
    }
}
```

- [ ] **Step 5: Build and commit**

```bash
dotnet build src/Outbox.Kafka/Outbox.Kafka.csproj
git add src/Outbox.Kafka/
git commit -m "feat: implement Kafka transport provider"
```

---

## Chunk 7: EventHub Transport

### Task 14: Create EventHub Transport Project and Implementation

**Files:**
- Create: `src/Outbox.EventHub/Outbox.EventHub.csproj`
- Create: `src/Outbox.EventHub/EventHubTransportOptions.cs`
- Create: `src/Outbox.EventHub/EventHubOutboxTransport.cs`
- Create: `src/Outbox.EventHub/EventHubOutboxBuilderExtensions.cs`

Follow the same pattern as Kafka (Task 13), but using `Azure.Messaging.EventHubs`:
- Use `EventHubProducerClient` for sending
- Create `EventDataBatch` with `SendEventOptions { PartitionKey = partitionKey }`
- Handle batch size limits (split if over `MaxBatchSizeBytes`)
- Deserialize `Headers` JSON and set as `EventData.Properties`

Port the EventHub-specific logic from the POC's `OutboxPublisher.cs`.

- [ ] **Step 1: Create project, options, transport, and builder extensions**

Follow same structure as Kafka task.

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/Outbox.EventHub/Outbox.EventHub.csproj
git add src/Outbox.EventHub/
git commit -m "feat: implement EventHub transport provider"
```

---

## Chunk 8: Final Verification

### Task 15: Full Solution Build and Cleanup

**Files:**
- Modify: `src/Outbox.sln` (ensure all projects are in the solution)

- [ ] **Step 1: Ensure all projects are in the solution**

```bash
cd /home/vgmello/repos/poc/poc/src
dotnet sln list
```

Verify all 5 source projects and all test projects are present. Add any missing ones.

- [ ] **Step 2: Full solution build**

```bash
dotnet build Outbox.sln
```

Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Run all tests**

```bash
dotnet test Outbox.sln --verbosity normal
```

Expected: All tests pass.

- [ ] **Step 4: Delete auto-generated Class1.cs files**

Each `dotnet new classlib` creates a `Class1.cs`. Delete all of them if they still exist.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: final build verification and cleanup"
```
