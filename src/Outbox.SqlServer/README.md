# Outbox.SqlServer

SQL Server store for the outbox library. Implements `IOutboxStore` and `IDeadLetterManager` using [Microsoft.Data.SqlClient](https://learn.microsoft.com/en-us/sql/connect/ado-net/microsoft-ado-net-sql-server) with hand-written T-SQL.

## Setup

```csharp
services.AddOutbox(configuration, outbox =>
{
    outbox.UseSqlServer();
    outbox.UseKafka();
});
```

The connection string and other options are bound from `"Outbox:SqlServer"` in `IConfiguration`. You can also pass an options callback for programmatic overrides:

```csharp
outbox.UseSqlServer(opts => opts.SchemaName = "events");
```

Run `db_scripts/install.sql` against your database to create the schema. The script is idempotent (`IF NOT EXISTS` guards throughout).

## Schema

Four tables, a TVP type, one nonclustered index, two diagnostic views, and a 64-partition seed:

| Table | Purpose |
|---|---|
| `dbo.Outbox` | Primary event buffer |
| `dbo.OutboxDeadLetter` | Messages dead-lettered after exhausting `MaxPublishAttempts` |
| `dbo.OutboxPublishers` | Heartbeat registry |
| `dbo.OutboxPartitions` | Partition-to-publisher ownership map |
| `dbo.SequenceNumberList` (TVP) | Table-valued parameter for batch operations |

### Key columns on `Outbox`

| Column | Type | Notes |
|---|---|---|
| `SequenceNumber` | `BIGINT IDENTITY` | Clustered PK, monotonic |
| `TopicName` | `NVARCHAR(256)` | Broker topic |
| `PartitionKey` | `NVARCHAR(256)` | Hash input for partition assignment |
| `Payload` | `VARBINARY(MAX)` | Raw message bytes |
| `Headers` | `NVARCHAR(2000)` | JSON-serialized headers (nullable) |
| `RowVersion` | `ROWVERSION` | Version ceiling for ordering safety |

### Indexes

- `IX_Outbox_Pending` — On `(PartitionId, SequenceNumber)` for insert-order polling

### Diagnostic views

`dbo.vw_Outbox` and `dbo.vw_OutboxDeadLetter` cast `Payload` as `VARCHAR(MAX)` for `application/json` and `text/plain` content types.

## How it works

### Partition hash function

```sql
ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % @TotalPartitions
```

`CHECKSUM()` is SQL Server's built-in hash. The `CAST` to `BIGINT` before `ABS()` handles `INT_MIN` safely. This produces different partition assignments than PostgreSQL's `hashtext()`—cross-provider migration would require rehashing.

### Fetch mechanism

`FetchBatchAsync` is a pure `SELECT` with no row-locking hints. The `ORDER BY PartitionId, SequenceNumber` aligns with the composite nonclustered index `IX_Outbox_Pending (PartitionId, SequenceNumber)`, enabling an Index Seek per owned partition with no sort operator. Per-`(topic, partition_key)` ordering is preserved because rows sharing a partition key share a `PartitionId` by construction — the outer `PartitionId` sort never splits a key's messages. A bare `ORDER BY SequenceNumber` would force a clustered-PK scan with per-row partition lookups (measured ~48× more logical reads on a 50k-row table). A version ceiling filter ensures ordering safety:

```sql
WHERE o.RowVersion < MIN_ACTIVE_ROWVERSION()
```

This withholds rows from in-flight write transactions, preventing the scenario where Transaction #2 commits before Transaction #1 and its rows are published out of order. Partition ownership is the sole isolation mechanism — there are no per-message leases.

### Dead-lettering

`DeadLetterAsync` uses `DELETE...OUTPUT...INTO` for an atomic move from `Outbox` to `OutboxDeadLetter` in a single statement.

### Rebalance

Single transactional T-SQL batch:

1. Compute fair share (`CEILING(total / active)`).
2. Grace-mark partitions owned by stale publishers (unconditional — runs every cycle so dead publishers' partitions start their grace timer as soon as any surviving publisher rebalances).
3. If `@ToAcquire > 0` (publisher is under fair share): claim unowned or grace-expired partitions up to fair share using `UPDLOCK, READPAST` to prevent concurrent double-claiming.
4. If `@CurrentlyOwned > @FairShare` (publisher is over fair share): release the excess.

### Transient error handling

Retries on SQL Server error numbers including:
- `1205` — Deadlock
- `-2`, `-1` — Timeouts and connection reset
- `233`, `10053`, `10054`, `10060` — Connection issues
- `10928`, `10929`, `40143`, `40197`, `40501`, `40540`, `40613`, `49918`, `49919`, `49920` — Azure SQL errors
- `IOException`/`SocketException` inner exceptions

Backoff: `base × 2^(attempt-1)` with up to 25% jitter.

## Configuration

Bind from `"Outbox:SqlServer"` in `IConfiguration`.

| Option | Default | Description |
|---|---|---|
| `ConnectionString` | null | SQL Server connection string |
| `SchemaName` | `"dbo"` | SQL Server schema name |
| `TablePrefix` | `""` | Prefix for all table names and TVP type |
| `CommandTimeoutSeconds` | `30` | SQL command timeout |
| `TransientRetryMaxAttempts` | `6` | Max retry attempts |
| `TransientRetryBackoffMs` | `1000` | Base backoff (ms) |

### Table prefix

With `TablePrefix = "Orders"`, objects become `dbo.OrdersOutbox`, `dbo.OrdersOutboxDeadLetter`, `dbo.OrdersSequenceNumberList`, etc. The `install.sql` script uses default names—adjust manually for non-default prefixes.

## Dead letter manager

`SqlServerDeadLetterManager` implements `IDeadLetterManager`:

- **Get** — Paginated read using `OFFSET...FETCH NEXT`, ordered by `DeadLetterSeq`
- **Replay** — Atomic `DELETE...OUTPUT...INTO` from dead-letter back to outbox (in-memory attempt counter starts fresh on next poll)
- **Purge/PurgeAll** — Permanent deletion via TVP join or full table delete

Replayed messages get a new `SequenceNumber` (the identity column regenerates).

## Azure SQL considerations

The expanded transient error list covers Azure SQL geo-failover scenarios. Ensure your retry budget covers the typical 20–30 second failover window:

```
TransientRetryBackoffMs × 2^(MaxAttempts-1) > 20000
```

With defaults (1000ms base, 6 attempts): `1000 × 2^5 = 32000ms` — sufficient.
