# Outbox.PostgreSQL

PostgreSQL store for the outbox library. Implements `IOutboxStore` and `IDeadLetterManager` using [Npgsql](https://www.npgsql.org/) with hand-written SQL.

## Setup

```csharp
services.AddOutbox(configuration, outbox =>
{
    outbox.UsePostgreSql(
        connectionFactory: async (sp, ct) =>
        {
            var conn = new NpgsqlConnection(connectionString);
            return conn;
        },
        configure: opts => opts.SchemaName = "events"
    );
    outbox.UseKafka();
});
```

Run `db_scripts/install.sql` against your database to create the schema. The script is idempotent.

## Schema

Four tables, three indexes, two diagnostic views, and a 64-partition seed:

| Table | Purpose |
|---|---|
| `outbox` | Primary event buffer with lease columns |
| `outbox_dead_letter` | Messages past `MaxRetryCount` |
| `outbox_publishers` | Heartbeat registry |
| `outbox_partitions` | Partition-to-publisher ownership map |

### Key columns on `outbox`

| Column | Type | Notes |
|---|---|---|
| `sequence_number` | `BIGINT IDENTITY` | PK, monotonic |
| `topic_name` | `VARCHAR(256)` | Broker topic |
| `partition_key` | `VARCHAR(256)` | Hash input for partition assignment |
| `payload` | `BYTEA` | Raw message bytes |
| `leased_until_utc` | `TIMESTAMPTZ(3)` | NULL = available |
| `lease_owner` | `VARCHAR(128)` | Publisher holding the lease |
| `retry_count` | `INT` | Delivery attempts |

### Indexes

- `ix_outbox_unleased` — Partial on `(event_datetime_utc, event_ordinal)` where `leased_until_utc IS NULL`
- `ix_outbox_lease_expiry` — Partial on `(leased_until_utc, event_datetime_utc, event_ordinal)` where `leased_until_utc IS NOT NULL`
- `ix_outbox_sweep` — On `(retry_count, lease_owner)` for dead-letter sweep

### Diagnostic views

`vw_outbox` and `vw_outbox_dead_letter` decode `payload` as UTF-8 text for `application/json` and `text/plain` content types.

## How it works

### Partition hash function

```sql
(hashtext(partition_key) & 2147483647) % total_partitions
```

`hashtext()` is PostgreSQL's built-in FNV-based 32-bit hash. The `& 2147483647` masks the sign bit. This produces different partition assignments than SQL Server's `CHECKSUM()`—cross-provider migration would require rehashing.

### Lease mechanism

`LeaseBatchAsync` uses a CTE with `FOR UPDATE SKIP LOCKED` to atomically select and stamp messages. The retry count is incremented only when re-leasing a previously-leased row (first-time leases don't consume a retry slot).

All mutating operations check `lease_owner = @publisher_id` to prevent zombie publishers from affecting rows they no longer own.

### Dead-lettering

`DeadLetterAsync` uses an atomic CTE: `DELETE ... RETURNING` piped into `INSERT INTO dead_letter`. No intermediate state where a message exists in neither table.

### Rebalance

Three-step transaction:
1. Mark stale publishers' partitions with a grace period
2. Claim unowned or grace-expired partitions up to the fair share
3. Release excess partitions

Uses `FOR UPDATE SKIP LOCKED` throughout to prevent concurrent publishers from double-claiming.

### Transient error handling

Retries on PostgreSQL error classes: `40xxx` (serialization/deadlock), `57xxx` (admin shutdown), `08xxx` (connection), `53xxx` (insufficient resources), plus `IOException`/`SocketException` inner exceptions.

Backoff: `base × 2^(attempt-1)` with up to 25% jitter.

## Configuration

Bind from `"Outbox:PostgreSql"` in `IConfiguration`.

| Option | Default | Description |
|---|---|---|
| `SchemaName` | `"public"` | PostgreSQL schema name |
| `TablePrefix` | `""` | Prefix for all table names |
| `CommandTimeoutSeconds` | `30` | SQL command timeout |
| `TransientRetryMaxAttempts` | `6` | Max retry attempts |
| `TransientRetryBackoffMs` | `1000` | Base backoff (ms) |

### Table prefix

With `TablePrefix = "orders_"`, tables become `public.orders_outbox`, `public.orders_outbox_dead_letter`, etc. Useful for multi-tenant scenarios. The `install.sql` script uses bare names—adjust manually for non-default prefixes.

## Dead letter manager

`PostgreSqlDeadLetterManager` implements `IDeadLetterManager`:

- **Get** — Paginated read ordered by `dead_letter_seq`
- **Replay** — Atomic delete-from-dead-letter + insert-into-outbox with `retry_count = 0`
- **Purge/PurgeAll** — Permanent deletion

Replayed messages get a new `sequence_number` (the identity column regenerates).
