# Config-driven store setup design

## Problem

The current `UsePostgreSql` / `UseSqlServer` API requires a connection factory delegate as a mandatory parameter, making the simplest setup verbose:

```csharp
outbox.UsePostgreSql(async (sp, ct) =>
{
    var conn = new NpgsqlConnection(
        sp.GetRequiredService<IConfiguration>().GetConnectionString("OutboxDb"));
    return conn;
}, options => options.TablePrefix = "orders_");
```

Additionally, publisher group configuration isn't scoped—all groups share the same `Outbox:Publisher` and `Outbox:PostgreSql` config paths, requiring programmatic overrides per group.

## Goal

1. Enable fully config-driven store setup where `UsePostgreSql()` / `UseSqlServer()` requires zero parameters
2. Scope config paths per publisher group using an Aspire-like pattern: `Outbox:{GroupName}:PostgreSql`
3. Support fallback from group-specific to shared config for common settings
4. Keep an escape hatch (`ConnectionFactory`) for advanced scenarios like token-based auth

## Design

### Store options changes

`PostgreSqlStoreOptions` and `SqlServerStoreOptions` each get two new properties:

```csharp
public string? ConnectionString { get; set; }

[System.Text.Json.Serialization.JsonIgnore]
public Func<IServiceProvider, CancellationToken, Task<DbConnection>>? ConnectionFactory { get; set; }
```

**Resolution order:**

1. `ConnectionFactory` if set—call it to get a `DbConnection`
2. `ConnectionString` if set—create `new NpgsqlConnection(ConnectionString)` or `new SqlConnection(ConnectionString)` internally
3. Neither—startup validation fails

**Validation:** Both options classes implement `IValidatableObject`. Add a cross-property rule: at least one of `ConnectionString` or `ConnectionFactory` must be set. Use `string.IsNullOrEmpty` for the `ConnectionString` check (empty string is not valid).

`ConnectionString` must **not** carry a `[Required]` attribute—the `ConnectionFactory`-only path must pass validation without a connection string.

`ConnectionString` binds from `appsettings.json`. `ConnectionFactory` is code-only (`[JsonIgnore]` prevents config binding from failing on the delegate type).

**Both set:** Valid. `ConnectionFactory` takes precedence.

**Hot-reload note:** Options are snapshotted at store construction time (singleton lifetime). Changes to `ConnectionString` via config reload are not picked up at runtime. This is consistent with how all other store options behave.

### DbHelper changes

`PostgreSqlDbHelper` and `SqlServerDbHelper` currently take a `Func<IServiceProvider, CancellationToken, Task<DbConnection>>` as a constructor parameter. This changes to accept the store options directly. The `DbHelper` derives the connection factory internally:

```csharp
// Inside DbHelper constructor or OpenConnectionAsync:
if (_options.ConnectionFactory is not null)
    conn = await _options.ConnectionFactory(sp, ct);
else
    conn = new NpgsqlConnection(_options.ConnectionString);
```

The `OpenConnectionAsync` method continues to handle opening the connection if it's returned in a closed state—this behavior is unchanged.

The `connectionFactory` singleton/keyed-singleton registration in DI is removed—`DbHelper` resolves connections from options instead.

### Config binding with group scoping

Config paths change based on group name:

| Scenario | Publisher | Store | Transport |
|----------|-----------|-------|-----------|
| Default (no group) | `Outbox:Publisher` | `Outbox:PostgreSql` | `Outbox:Kafka` |
| Group "orders" | `Outbox:Orders:Publisher` | `Outbox:Orders:PostgreSql` | `Outbox:Orders:Kafka` |

**Group names in config keys are case-insensitive** (by virtue of `IConfiguration`). `AddOutbox("orders", ...)` matches `"Orders"` in JSON.

**Fallback:** Named groups bind to both paths—shared first, group-specific second (last-write-wins):

```csharp
builder.Services.AddOptions<PostgreSqlStoreOptions>(groupName)
    .BindConfiguration("Outbox:PostgreSql")                   // shared fallback
    .BindConfiguration($"Outbox:{groupName}:PostgreSql")      // group override
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

This applies to all config sections consistently:

- `OutboxPublisherOptions`: `Outbox:Publisher` → `Outbox:{GroupName}:Publisher`
- `PostgreSqlStoreOptions`: `Outbox:PostgreSql` → `Outbox:{GroupName}:PostgreSql`
- `SqlServerStoreOptions`: `Outbox:SqlServer` → `Outbox:{GroupName}:SqlServer`
- `KafkaTransportOptions`: `Outbox:Kafka` → `Outbox:{GroupName}:Kafka`
- `EventHubTransportOptions`: `Outbox:EventHub` → `Outbox:{GroupName}:EventHub`

**Kafka and EventHub migration:** The current Kafka and EventHub extensions use `builder.Services.Configure<T>(builder.Configuration.GetSection(...))` instead of `AddOptions<T>().BindConfiguration()`. These must be refactored to use `AddOptions<T>().BindConfiguration()` to support the chained fallback pattern. This is a larger change than the store extensions since the entire options binding approach changes for these transports.

**Result:** Common settings (e.g., `BootstrapServers`) live once at `Outbox:Kafka`. Group-specific settings (e.g., `ConnectionString`, `TablePrefix`) live at `Outbox:{GroupName}:PostgreSql`.

### API changes

**`UsePostgreSql` / `UseSqlServer` new signatures:**

```csharp
// Simple—everything from config
outbox.UsePostgreSql();

// With options callback (for ConnectionFactory or programmatic overrides)
outbox.UsePostgreSql(options =>
{
    options.ConnectionFactory = async (sp, ct) => { ... };
});
```

The old signature with the factory delegate as the first parameter is **removed** (no backwards compatibility).

### Config examples

**Single publisher (default group):**

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

**Multiple publisher groups:**

```json
{
    "Outbox": {
        "Kafka": {
            "BootstrapServers": "localhost:9092"
        },
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

Shared `Kafka:BootstrapServers` is inherited by both groups. Each group overrides its own `PostgreSql` section.

## Changes

### Files to modify

| File | Change |
|------|--------|
| `src/Outbox.PostgreSQL/PostgreSqlStoreOptions.cs` | Add `ConnectionString`, `ConnectionFactory`, `IValidatableObject` validation (no `[Required]` on `ConnectionString`) |
| `src/Outbox.SqlServer/SqlServerStoreOptions.cs` | Same |
| `src/Outbox.PostgreSQL/PostgreSqlDbHelper.cs` | Accept store options instead of raw factory delegate, derive connection from `ConnectionFactory ?? ConnectionString` |
| `src/Outbox.SqlServer/SqlServerDbHelper.cs` | Same |
| `src/Outbox.PostgreSQL/PostgreSqlOutboxBuilderExtensions.cs` | Remove old factory signature, add parameterless + options-only overloads, group-scoped `BindConfiguration`, remove `connectionFactory` DI registration |
| `src/Outbox.SqlServer/SqlServerOutboxBuilderExtensions.cs` | Same |
| `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs` | Update constructor—no longer receives factory from DI, passes options to `DbHelper` |
| `src/Outbox.SqlServer/SqlServerOutboxStore.cs` | Same |
| `src/Outbox.PostgreSQL/PostgreSqlDeadLetterManager.cs` | Same constructor update |
| `src/Outbox.SqlServer/SqlServerDeadLetterManager.cs` | Same |
| `src/Outbox.Core/Builder/OutboxServiceCollectionExtensions.cs` | Group-scoped `BindConfiguration` for `OutboxPublisherOptions` |
| `src/Outbox.Kafka/KafkaOutboxBuilderExtensions.cs` | Migrate from `Configure<T>(IConfigurationSection)` to `AddOptions<T>().BindConfiguration()`, add group-scoped fallback |
| `src/Outbox.EventHub/EventHubOutboxBuilderExtensions.cs` | Same migration |
| `README.md` | Rewrite setup examples to config-driven |
| `docs/architecture.md` | Update config reference table |
| `src/Outbox.PostgreSQL/README.md` | Update examples |
| `src/Outbox.SqlServer/README.md` | Update examples |

### Tests to update

**Store options validation tests** — four cases:
1. `ConnectionString` set, no `ConnectionFactory` → valid
2. `ConnectionFactory` set, no `ConnectionString` → valid
3. Both set → valid (`ConnectionFactory` wins)
4. Neither set → invalid
5. Empty string `ConnectionString`, no `ConnectionFactory` → invalid

**Integration test helpers** — `ToggleableConnectionFactory` must migrate to `options.ConnectionFactory = toggleable.CreateConnectionAsync` (not removed—the toggleable pattern is essential for DB outage simulation tests).

**Builder extension tests** — new parameterless and options-only signatures.

### No changes to

- `IOutboxStore` interface
- SQL queries
- Publish loop / parallel workers
- Circuit breaker / rebalance / health
- `OutboxMessage` model
- Partition table schema
