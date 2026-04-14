# Outbox.PostgreSQL

PostgreSQL store for the outbox library. Implements `IOutboxStore` and `IDeadLetterManager` using [Npgsql](https://www.npgsql.org/) with hand-written SQL.

## Setup

```csharp
services.AddOutbox(configuration, outbox =>
{
    outbox.UsePostgreSql();
    outbox.UseKafka();
});
```

The connection string and other options are bound from `"Outbox:PostgreSql"` in `IConfiguration`. You can also pass an options callback for programmatic overrides:

```csharp
outbox.UsePostgreSql(opts => opts.SchemaName = "events");
```

Run `db_scripts/install.sql` against your database to create the schema. The script is idempotent.

## Schema

Four tables, one index, two diagnostic views, and a 64-partition seed:

| Table | Purpose |
|---|---|
| `outbox` | Primary event buffer |
| `outbox_dead_letter` | Messages dead-lettered after exhausting `MaxPublishAttempts` |
| `outbox_publishers` | Heartbeat registry |
| `outbox_partitions` | Partition-to-publisher ownership map |

### Key columns on `outbox`

| Column | Type | Notes |
|---|---|---|
| `sequence_number` | `BIGINT IDENTITY` | PK, monotonic |
| `topic_name` | `VARCHAR(256)` | Broker topic |
| `partition_key` | `VARCHAR(256)` | Hash input for partition assignment |
| `payload` | `BYTEA` | Raw message bytes |
| `headers` | `VARCHAR(2000)` | JSON-serialized headers (nullable) |
| `event_ordinal` | `INT` | Tiebreaker for same-timestamp events |

### Indexes

- `ix_outbox_pending` — On `(event_datetime_utc, event_ordinal)` for causal-order polling

### Diagnostic views

`vw_outbox` and `vw_outbox_dead_letter` decode `payload` as UTF-8 text for `application/json` and `text/plain` content types.

## How it works

### Partition hash function

```sql
(hashtext(partition_key) & 2147483647) % total_partitions
```

`hashtext()` is PostgreSQL's built-in FNV-based 32-bit hash. The `& 2147483647` masks the sign bit. This produces different partition assignments than SQL Server's `CHECKSUM()`—cross-provider migration would require rehashing.

### Fetch mechanism

`FetchBatchAsync` is a pure `SELECT` — no row locking or updating. It reads messages from owned partitions ordered by `(event_datetime_utc, event_ordinal)` with a version ceiling filter:

```sql
WHERE o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
```

This withholds rows from in-flight write transactions, preventing the scenario where Transaction #2 commits before Transaction #1 and its rows are published out of order. Partition ownership is the sole isolation mechanism — there are no per-message leases.

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
| `ConnectionString` | null | PostgreSQL connection string |
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
- **Replay** — Atomic delete-from-dead-letter + insert-into-outbox (in-memory attempt counter starts fresh on next poll)
- **Purge/PurgeAll** — Permanent deletion

Replayed messages get a new `sequence_number` (the identity column regenerates).
