# Config-driven store setup implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move connection setup into store options (with `ConnectionString` + `ConnectionFactory`), add Aspire-like group-scoped config binding (`Outbox:{GroupName}:*`), and simplify the `UsePostgreSql`/`UseSqlServer` API to zero required parameters.

**Architecture:** Store options gain `ConnectionString` (config-bindable) and `ConnectionFactory` (code-only). `DbHelper` derives the connection from options instead of a DI-injected factory. All config sections (`Publisher`, `PostgreSql`, `SqlServer`, `Kafka`, `EventHub`) bind with group-scoped fallback: shared `Outbox:*` first, then `Outbox:{GroupName}:*` override.

**Tech Stack:** .NET 8, xUnit, NSubstitute, Dapper, Npgsql, Microsoft.Data.SqlClient

**Spec:** `docs/superpowers/specs/2026-03-26-config-driven-store-setup-design.md`

---

### Task 1: Add `ConnectionString` and `ConnectionFactory` to PostgreSQL store options with validation

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlStoreOptions.cs`
- Test: `tests/Outbox.Store.Tests/PostgreSqlStoreOptionsTests.cs`

- [ ] **Step 1: Write failing validation tests**

In `tests/Outbox.Store.Tests/PostgreSqlStoreOptionsTests.cs`, add 5 new tests:

```csharp
[Fact]
public void ConnectionString_set_without_factory_is_valid()
{
    var opts = new PostgreSqlStoreOptions { ConnectionString = "Host=localhost;Database=test" };
    var results = Validate(opts);
    Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)));
}

[Fact]
public void ConnectionFactory_set_without_connectionString_is_valid()
{
    var opts = new PostgreSqlStoreOptions
    {
        ConnectionFactory = (_, _) => Task.FromResult<DbConnection>(new NpgsqlConnection())
    };
    var results = Validate(opts);
    Assert.Empty(results.Where(r =>
        r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)) ||
        r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionFactory))));
}

[Fact]
public void Both_connectionString_and_factory_set_is_valid()
{
    var opts = new PostgreSqlStoreOptions
    {
        ConnectionString = "Host=localhost;Database=test",
        ConnectionFactory = (_, _) => Task.FromResult<DbConnection>(new NpgsqlConnection())
    };
    var results = Validate(opts);
    Assert.Empty(results.Where(r =>
        r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)) ||
        r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionFactory))));
}

[Fact]
public void Neither_connectionString_nor_factory_is_invalid()
{
    var opts = new PostgreSqlStoreOptions();
    var results = Validate(opts);
    Assert.Contains(results, r => r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)));
}

[Fact]
public void Empty_connectionString_without_factory_is_invalid()
{
    var opts = new PostgreSqlStoreOptions { ConnectionString = "" };
    var results = Validate(opts);
    Assert.Contains(results, r => r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)));
}
```

Use the existing `Validate` helper in the test file.

- [ ] **Step 2: Run tests — verify they fail**

Run: `dotnet test tests/Outbox.Store.Tests/ --filter "ConnectionString" -v n`
Expected: compilation errors (properties don't exist yet).

- [ ] **Step 3: Add properties and validation to `PostgreSqlStoreOptions`**

In `src/Outbox.PostgreSQL/PostgreSqlStoreOptions.cs`:

1. Add `using System.Data.Common;` and `using System.Text.Json.Serialization;` to imports
2. Make the class implement `IValidatableObject` (add `using System.ComponentModel.DataAnnotations;` if not present)
3. After `SharedSchemaName` property, add:

```csharp
public string? ConnectionString { get; set; }

[JsonIgnore]
public Func<IServiceProvider, CancellationToken, Task<DbConnection>>? ConnectionFactory { get; set; }
```

4. Add `Validate` method:

```csharp
public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
{
    if (ConnectionFactory is null && string.IsNullOrEmpty(ConnectionString))
    {
        yield return new ValidationResult(
            "Either ConnectionString or ConnectionFactory must be set.",
            new[] { nameof(ConnectionString), nameof(ConnectionFactory) });
    }
}
```

**Important:** Do NOT add `[Required]` to `ConnectionString` — the `ConnectionFactory`-only path must pass validation.

- [ ] **Step 4: Run tests — verify they pass**

Run: `dotnet test tests/Outbox.Store.Tests/ -v n`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.PostgreSQL/PostgreSqlStoreOptions.cs tests/Outbox.Store.Tests/PostgreSqlStoreOptionsTests.cs
git commit -m "feat(postgresql): add ConnectionString and ConnectionFactory to store options with validation"
```

---

### Task 2: Add `ConnectionString` and `ConnectionFactory` to SQL Server store options with validation

**Files:**
- Modify: `src/Outbox.SqlServer/SqlServerStoreOptions.cs`
- Test: `tests/Outbox.Store.Tests/SqlServerStoreOptionsTests.cs`

Same pattern as Task 1 but for SQL Server. Use `SqlConnection` instead of `NpgsqlConnection` in tests.

- [ ] **Step 1: Write failing validation tests** (same 5 tests, adapted for `SqlServerStoreOptions` / `SqlConnection`)
- [ ] **Step 2: Run tests — verify they fail**
- [ ] **Step 3: Add properties and validation to `SqlServerStoreOptions`** (same as Task 1 pattern)
- [ ] **Step 4: Run tests — verify they pass**

Run: `dotnet test tests/Outbox.Store.Tests/ -v n`

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.SqlServer/SqlServerStoreOptions.cs tests/Outbox.Store.Tests/SqlServerStoreOptionsTests.cs
git commit -m "feat(sqlserver): add ConnectionString and ConnectionFactory to store options with validation"
```

---

### Task 3: Update `PostgreSqlDbHelper` to derive connection from options

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlDbHelper.cs`

- [ ] **Step 1: Run baseline tests**

Run: `dotnet test tests/Outbox.Store.Tests/ -v n`

- [ ] **Step 2: Replace constructor factory parameter with options-based resolution**

In `src/Outbox.PostgreSQL/PostgreSqlDbHelper.cs`:

1. Remove the `_connectionFactory` field (line 11)
2. Remove the `connectionFactory` parameter from the constructor — keep `IServiceProvider serviceProvider` and `PostgreSqlStoreOptions options`. The new constructor signature is `PostgreSqlDbHelper(IServiceProvider serviceProvider, PostgreSqlStoreOptions options)`. The `_serviceProvider` field stays — it's needed by `ConnectionFactory` calls.
3. Update `OpenConnectionAsync` (line 25-32) to resolve from options:

```csharp
public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
{
    DbConnection conn;

    if (_options.ConnectionFactory is not null)
        conn = await _options.ConnectionFactory(_serviceProvider, ct).ConfigureAwait(false);
    else
        conn = new NpgsqlConnection(_options.ConnectionString);

    if (conn.State != System.Data.ConnectionState.Open)
        await conn.OpenAsync(ct).ConfigureAwait(false);

    return conn;
}
```

**Note:** Do NOT commit yet — this breaks compilation in `PostgreSqlOutboxStore` and `PostgreSqlDeadLetterManager`. Task 4 fixes those callers and commits everything together.

---

### Task 4: Update PostgreSQL store, dead-letter manager, and builder extensions

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs`
- Modify: `src/Outbox.PostgreSQL/PostgreSqlDeadLetterManager.cs`
- Modify: `src/Outbox.PostgreSQL/PostgreSqlOutboxBuilderExtensions.cs`

- [ ] **Step 1: Update `PostgreSqlOutboxStore` constructor**

Remove the `connectionFactory` parameter. Pass options to `DbHelper` instead:

```csharp
public PostgreSqlOutboxStore(
    IServiceProvider serviceProvider,
    IOptionsMonitor<PostgreSqlStoreOptions> optionsMonitor,
    IOptionsMonitor<OutboxPublisherOptions> publisherOptions,
    string? groupName = null)
{
    _optionsName = groupName ?? Options.DefaultName;
    _options = optionsMonitor.Get(_optionsName);
    _publisherOptions = publisherOptions;
    _db = new PostgreSqlDbHelper(serviceProvider, _options);
    _queries = new PostgreSqlQueries(
        _options.SchemaName, _options.TablePrefix,
        _options.GetSharedSchemaName(), _options.GetOutboxTableName());
    DapperConfiguration.EnsureInitialized();
}
```

- [ ] **Step 2: Update `PostgreSqlDeadLetterManager` constructor**

Same pattern — remove `connectionFactory` parameter:

```csharp
public PostgreSqlDeadLetterManager(
    IServiceProvider serviceProvider,
    IOptionsMonitor<PostgreSqlStoreOptions> optionsMonitor,
    string? groupName = null)
{
    _options = optionsMonitor.Get(groupName ?? Options.DefaultName);
    _db = new PostgreSqlDbHelper(serviceProvider, _options);
    _queries = new PostgreSqlQueries(
        _options.SchemaName, _options.TablePrefix,
        _options.GetSharedSchemaName(), _options.GetOutboxTableName());
    DapperConfiguration.EnsureInitialized();
}
```

- [ ] **Step 3: Rewrite `PostgreSqlOutboxBuilderExtensions`**

Replace the entire `UsePostgreSql` method. New signatures:

```csharp
// Parameterless — everything from config
public static IOutboxBuilder UsePostgreSql(this IOutboxBuilder builder)
    => builder.UsePostgreSql(configure: null);

// Options callback
public static IOutboxBuilder UsePostgreSql(
    this IOutboxBuilder builder,
    Action<PostgreSqlStoreOptions>? configure = null)
```

The single implementation method handles both grouped and default paths:

- **Grouped path:** Uses dual `BindConfiguration` for fallback:
  ```csharp
  builder.Services.AddOptions<PostgreSqlStoreOptions>(groupName)
      .BindConfiguration("Outbox:PostgreSql")
      .BindConfiguration($"Outbox:{groupName}:PostgreSql")
      .ValidateDataAnnotations()
      .ValidateOnStart();
  ```
- **Default path:** Single `BindConfiguration("Outbox:PostgreSql")`
- Remove `connectionFactory` from DI registration
- Update `IOutboxStore` and `IDeadLetterManager` factory registrations — no longer pass factory, just `serviceProvider` and `optionsMonitor`

- [ ] **Step 4: Build PostgreSQL projects to verify compilation**

Run: `dotnet build src/Outbox.PostgreSQL/ -v q`
Expected: build succeeds. The full solution (`src/Outbox.slnx`) will still have compilation errors in integration tests — that's expected and fixed in Task 9.

- [ ] **Step 5: Commit** (includes DbHelper changes from Task 3)

```bash
git add src/Outbox.PostgreSQL/
git commit -m "feat(postgresql): config-driven setup with group-scoped binding"
```

---

### Task 5: Update SQL Server DbHelper, store, dead-letter manager, and builder extensions

**Files:**
- Modify: `src/Outbox.SqlServer/SqlServerDbHelper.cs`
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs`
- Modify: `src/Outbox.SqlServer/SqlServerDeadLetterManager.cs`
- Modify: `src/Outbox.SqlServer/SqlServerOutboxBuilderExtensions.cs`

Same pattern as Tasks 3-4 but for SQL Server. Use `SqlConnection` instead of `NpgsqlConnection` in `DbHelper.OpenConnectionAsync`.

- [ ] **Step 1: Update `SqlServerDbHelper`** — remove `_connectionFactory` field and `connectionFactory` constructor parameter. Keep `IServiceProvider serviceProvider` and `SqlServerStoreOptions options`. New constructor: `SqlServerDbHelper(IServiceProvider serviceProvider, SqlServerStoreOptions options)`. Update `OpenConnectionAsync` to resolve from `_options.ConnectionFactory ?? new SqlConnection(_options.ConnectionString)`, same pattern as Task 3
- [ ] **Step 2: Update `SqlServerOutboxStore` constructor** — remove `connectionFactory` parameter
- [ ] **Step 3: Update `SqlServerDeadLetterManager` constructor** — remove `connectionFactory` parameter
- [ ] **Step 4: Rewrite `SqlServerOutboxBuilderExtensions`** — same dual-bind pattern, remove factory DI registration
- [ ] **Step 5: Build SQL Server projects to verify compilation**

Run: `dotnet build src/Outbox.SqlServer/ -v q`
Expected: build succeeds. Full solution still has integration test compilation errors (Task 9).

- [ ] **Step 6: Commit**

```bash
git add src/Outbox.SqlServer/
git commit -m "feat(sqlserver): config-driven setup with group-scoped binding"
```

---

### Task 6: Add group-scoped config binding for publisher options

**Files:**
- Modify: `src/Outbox.Core/Builder/OutboxServiceCollectionExtensions.cs`

- [ ] **Step 1: Update the grouped `AddOutbox` method**

In `OutboxServiceCollectionExtensions.cs`, in the `AddOutbox(string groupName, ...)` method (line 55-60), add the fallback binding:

```csharp
services.AddOptions<OutboxPublisherOptions>(groupName)
    .BindConfiguration("Outbox:Publisher")                    // shared fallback
    .BindConfiguration($"Outbox:{groupName}:Publisher")       // group override
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

The default (non-grouped) `AddOutbox` keeps its single `BindConfiguration("Outbox:Publisher")` unchanged.

- [ ] **Step 2: Build and run core tests**

Run: `dotnet build src/Outbox.slnx -v q && dotnet test tests/Outbox.Core.Tests/ -v q`

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.Core/Builder/OutboxServiceCollectionExtensions.cs
git commit -m "feat(core): add group-scoped config binding for publisher options"
```

---

### Task 7: Migrate Kafka and EventHub extensions to `BindConfiguration` with group-scoped fallback

**Files:**
- Modify: `src/Outbox.Kafka/KafkaOutboxBuilderExtensions.cs`
- Modify: `src/Outbox.EventHub/EventHubOutboxBuilderExtensions.cs`

These currently use `builder.Services.Configure<T>(groupName, builder.Configuration.GetSection(...))`. Migrate to `AddOptions<T>().BindConfiguration()` to support the chained fallback pattern.

- [ ] **Step 1: Update Kafka first overload — grouped path (lines 26-27)**

Replace:
```csharp
builder.Services.Configure<KafkaTransportOptions>(groupName,
    builder.Configuration.GetSection(ConfigSection));
```

With:
```csharp
builder.Services.AddOptions<KafkaTransportOptions>(groupName)
    .BindConfiguration("Outbox:Kafka")
    .BindConfiguration($"Outbox:{groupName}:Kafka");
```

Keep the `if (configure is not null) builder.Services.Configure(groupName, configure);` line after.

- [ ] **Step 2: Update Kafka first overload — default path**

Replace the default `Configure<KafkaTransportOptions>(builder.Configuration.GetSection(...))` with:

```csharp
builder.Services.AddOptions<KafkaTransportOptions>()
    .BindConfiguration("Outbox:Kafka");
```

- [ ] **Step 2b: Update Kafka second overload (producerFactory variant, ~line 95)**

The second `UseKafka` overload that accepts `Func<IServiceProvider, IProducer<string, byte[]>> producerFactory` also uses `Configure<KafkaTransportOptions>(groupName, builder.Configuration.GetSection(...))` at ~line 104. Apply the exact same `BindConfiguration` migration to both its grouped and default paths.

- [ ] **Step 3: Update EventHub grouped path** — same pattern with `"Outbox:EventHub"` / `$"Outbox:{groupName}:EventHub"`

- [ ] **Step 4: Update EventHub default path** — same pattern

- [ ] **Step 5: Build**

Run: `dotnet build src/Outbox.slnx -v q`

- [ ] **Step 6: Commit**

```bash
git add src/Outbox.Kafka/KafkaOutboxBuilderExtensions.cs src/Outbox.EventHub/EventHubOutboxBuilderExtensions.cs
git commit -m "feat(kafka,eventhub): migrate to BindConfiguration with group-scoped fallback"
```

---

### Task 8: Run unit tests and fix compilation issues

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test tests/Outbox.Core.Tests/ && dotnet test tests/Outbox.Kafka.Tests/ && dotnet test tests/Outbox.EventHub.Tests/ && dotnet test tests/Outbox.Store.Tests/`

Fix any compilation or test failures caused by the constructor signature changes.

- [ ] **Step 2: Commit fixes if needed**

---

### Task 9: Update integration test helpers

**Files:**
- Modify: `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs`
- Modify: `tests/Outbox.IntegrationTests/Helpers/SqlServerTestHelper.cs`

Note: `ToggleableConnectionFactory.cs` and `SqlServerToggleableConnectionFactory.cs` do NOT need changes—their `CreateConnectionAsync` signature already matches the new `ConnectionFactory` delegate type.

- [ ] **Step 1: Update `OutboxTestHelper.BuildPublisherHost`**

Change line 63 from:
```csharp
outbox.UsePostgreSql(connectionFactory.CreateConnectionAsync);
```

To:
```csharp
outbox.UsePostgreSql(options =>
{
    options.ConnectionFactory = connectionFactory.CreateConnectionAsync;
});
```

- [ ] **Step 2: Update `SqlServerTestHelper.BuildPublisherHost`**

Same pattern — change `outbox.UseSqlServer(connectionFactory.CreateConnectionAsync)` to:

```csharp
outbox.UseSqlServer(options =>
{
    options.ConnectionFactory = connectionFactory.CreateConnectionAsync;
});
```

- [ ] **Step 3: Build integration tests**

Run: `dotnet build tests/Outbox.IntegrationTests/ -v q`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.IntegrationTests/
git commit -m "test: migrate integration test helpers to options-based connection factory"
```

---

### Task 10: Update documentation

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `src/Outbox.PostgreSQL/README.md`
- Modify: `src/Outbox.SqlServer/README.md`

- [ ] **Step 1: Rewrite `README.md`**

Update all setup examples to config-driven. The main setup example becomes:

```json
{
    "Outbox": {
        "Publisher": {
            "BatchSize": 100,
            "MaxRetryCount": 5,
            "PublishThreadCount": 4
        },
        "PostgreSql": {
            "ConnectionString": "Host=localhost;Database=myapp;Username=postgres;Password=..."
        },
        "Kafka": {
            "BootstrapServers": "localhost:9092"
        }
    }
}
```

```csharp
builder.Services.AddOutbox(builder.Configuration, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});
```

Update the publisher groups section with group-scoped config:

```json
{
    "Outbox": {
        "Kafka": { "BootstrapServers": "localhost:9092" },
        "Orders": {
            "Publisher": { "PublishThreadCount": 8 },
            "PostgreSql": {
                "ConnectionString": "Host=localhost;Database=myapp;...",
                "TablePrefix": "orders_"
            }
        },
        "Notifications": {
            "Publisher": { "PublishThreadCount": 2 },
            "PostgreSql": {
                "ConnectionString": "Host=localhost;Database=myapp;...",
                "TablePrefix": "notifications_"
            }
        }
    }
}
```

```csharp
builder.Services.AddOutbox("orders", builder.Configuration, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});

builder.Services.AddOutbox("notifications", builder.Configuration, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});
```

Also update the SQL Server + EventHub example similarly.

- [ ] **Step 2: Update `docs/architecture.md`** — update config reference table to show `ConnectionString` in store options and group-scoped binding paths

- [ ] **Step 3: Update `src/Outbox.PostgreSQL/README.md`** — update examples to show config-driven setup

- [ ] **Step 4: Update `src/Outbox.SqlServer/README.md`** — same

- [ ] **Step 5: Commit**

```bash
git add README.md docs/architecture.md src/Outbox.PostgreSQL/README.md src/Outbox.SqlServer/README.md
git commit -m "docs: update all examples to config-driven store setup with group-scoped binding"
```

---

### Task 11: Full build and test verification

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test tests/Outbox.Core.Tests/ && dotnet test tests/Outbox.Kafka.Tests/ && dotnet test tests/Outbox.EventHub.Tests/ && dotnet test tests/Outbox.Store.Tests/`
Expected: all PASS.

- [ ] **Step 2: Build entire solution**

Run: `dotnet build src/Outbox.slnx -v q`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Fix any issues and commit**
