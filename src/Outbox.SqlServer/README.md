# Outbox.SqlServer

SQL Server store for the outbox library. Implements `IOutboxStore` and `IDeadLetterManager` using [Microsoft.Data.SqlClient](https://learn.microsoft.com/en-us/sql/connect/ado-net/microsoft-ado-net-sql-server) with hand-written T-SQL.

## Setup

```csharp
services.AddOutbox(configuration, outbox =>
{
    outbox.UseSqlServer(
        connectionFactory: async (sp, ct) =>
        {
            var conn = new SqlConnection(connectionString);
            return conn;
        },
        configure: opts => opts.SchemaName = "events"
    );
    outbox.UseKafka();
});
```

Run `db_scripts/install.sql` against your database to create the schema. The script is idempotent (`IF NOT EXISTS` guards throughout).

## Schema

Four tables, a TVP type, three indexes, two diagnostic views, and a 64-partition seed:

| Table | Purpose |
|---|---|
| `dbo.Outbox` | Primary event buffer with lease columns |
| `dbo.OutboxDeadLetter` | Messages past `MaxRetryCount` |
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
| `LeasedUntilUtc` | `DATETIME2(3)` | NULL = available |
| `LeaseOwner` | `NVARCHAR(128)` | Publisher holding the lease |
| `RetryCount` | `INT` | Delivery attempts |

### Indexes

- `IX_Outbox_Unleased` — Filtered on `(EventDateTimeUtc, EventOrdinal)` where `LeasedUntilUtc IS NULL`
- `IX_Outbox_LeaseExpiry` — Filtered on `(LeasedUntilUtc, EventDateTimeUtc, EventOrdinal)` where `LeasedUntilUtc IS NOT NULL`
- `IX_Outbox_Sweep` — On `(RetryCount, LeaseOwner)` for dead-letter sweep

### Diagnostic views

`dbo.vw_Outbox` and `dbo.vw_OutboxDeadLetter` cast `Payload` as `VARCHAR(MAX)` for `application/json` and `text/plain` content types.

## How it works

### Partition hash function

```sql
ABS(CAST(CHECKSUM(PartitionKey) AS BIGINT)) % @TotalPartitions
```

`CHECKSUM()` is SQL Server's built-in hash. The `CAST` to `BIGINT` before `ABS()` handles `INT_MIN` safely. This produces different partition assignments than PostgreSQL's `hashtext()`—cross-provider migration would require rehashing.

### Lease mechanism

`LeaseBatchAsync` uses a CTE-based `UPDATE...OUTPUT` with `ROWLOCK, READPAST` hints. `READPAST` skips rows locked by other publishers instead of blocking. The retry count is incremented only on re-lease of previously-leased rows.

All mutating operations use a `SequenceNumberList` TVP join and check `LeaseOwner = @PublisherId`.

### Dead-lettering

`DeadLetterAsync` uses `DELETE...OUTPUT...INTO` for an atomic move from `Outbox` to `OutboxDeadLetter` in a single statement.

### Rebalance

Single transactional T-SQL batch:
1. Compute fair share (`CEILING(total / active)`)
2. Mark stale publishers' partitions with grace period
3. Claim unowned or grace-expired partitions up to fair share
4. Release excess partitions

Uses `UPDLOCK, READPAST` to prevent concurrent double-claiming.

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
- **Replay** — Atomic `DELETE...OUTPUT...INTO` from dead-letter back to outbox with `RetryCount = 0`
- **Purge/PurgeAll** — Permanent deletion via TVP join or full table delete

Replayed messages get a new `SequenceNumber` (the identity column regenerates).

## Azure SQL considerations

The expanded transient error list covers Azure SQL geo-failover scenarios. Ensure your retry budget covers the typical 20–30 second failover window:

```
TransientRetryBackoffMs × 2^(MaxAttempts-1) > 20000
```

With defaults (1000ms base, 6 attempts): `1000 × 2^5 = 32000ms` — sufficient.
