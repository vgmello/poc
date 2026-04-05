# SQL Server Ghost Record Maintenance — Design Spec

## Problem

The outbox pattern generates a continuous stream of DELETE operations as published messages are removed. SQL Server marks deleted rows as "ghost records" for lazy background cleanup rather than removing them immediately. Under normal load the built-in ghost cleanup task keeps pace, but after prolonged broker outages (hours) or sustained high throughput (10K+ msg/sec), ghost records can accumulate faster than cleanup, causing `IX_Outbox_Pending` to bloat and poll latency to degrade.

The library currently has no maintenance scripts, monitoring guidance, or documentation for this SQL Server-specific characteristic.

## Scope

**In scope:**
- 4 maintenance SQL scripts for operators to schedule via SQL Agent, Azure Elastic Job, or equivalent
- Runbook section in `docs/production-runbook.md` with procedures for each script
- Known limitation entry in `docs/known-limitations.md`

**Out of scope:**
- Application code changes (no new background loops, no new metrics)
- PostgreSQL maintenance (PostgreSQL's VACUUM handles this natively)
- Automated scheduling from within the library

## Deliverables

### 1. Maintenance Scripts

Location: `src/Outbox.SqlServer/db_scripts/maintenance/`

All scripts are parameterized with `@SchemaName` (default `dbo`) and `@TablePrefix` (default empty string) to match `SqlServerStoreOptions`.

#### 1a. `check_ghost_records.sql`

**Purpose:** Diagnostic script to assess ghost record count and index fragmentation.

**Behavior:**
- Queries `sys.dm_db_index_physical_stats` in `LIMITED` mode (fast, no full scan) for the Outbox table
- Reports per-index: index name, ghost record count, fragmentation %, page count, average fragment size in pages
- Detects SQL Server edition via `SERVERPROPERTY('EngineEdition')` for Azure SQL compatibility
- Outputs a recommendation column based on thresholds:
  - `OK` — fragmentation < 10%, ghost records < 1000
  - `REORGANIZE` — fragmentation 10-30% or ghost records 1000-10000
  - `REBUILD` — fragmentation > 30% or ghost records > 10000
  - `INVESTIGATE` — page count < 1000 (too small for fragmentation to matter, but ghost count is high)
- Also reports table row count and space used via `sys.dm_db_partition_stats`

**Required permissions:** `VIEW DATABASE STATE`

**Targets:**
- `PK_Outbox` (clustered index on `SequenceNumber`)
- `IX_Outbox_Pending` (nonclustered covering index)
- `PK_OutboxDeadLetter` (clustered index, for completeness)
- `IX_OutboxDeadLetter_SequenceNumber` (nonclustered index)

#### 1b. `reorganize_indexes.sql`

**Purpose:** Online index defragmentation that removes ghost records and compacts pages.

**Behavior:**
- Targets `PK_Outbox` and `IX_Outbox_Pending` (the two indexes on the main outbox table)
- `ALTER INDEX ... REORGANIZE` — always online, no blocking, no edition requirement
- Optionally reorganizes `PK_OutboxDeadLetter` and `IX_OutboxDeadLetter_SequenceNumber` (controlled by `@IncludeDeadLetter` parameter, default 0)
- Runs `UPDATE STATISTICS` on affected indexes after reorganize (ensures query optimizer has accurate cardinality estimates post-compaction)
- Prints before/after fragmentation % and ghost record count for verification
- Idempotent — safe to run at any time, even during active publishing

**Required permissions:** `ALTER` on the table

**Impact:** Online operation. Minimal CPU overhead. Safe to run during business hours.

#### 1c. `rebuild_indexes.sql`

**Purpose:** Full index rebuild for severe fragmentation (>30%) or after major outage recovery.

**Behavior:**
- Targets same indexes as reorganize, plus dead-letter indexes when `@IncludeDeadLetter = 1`
- Detects edition via `SERVERPROPERTY('EngineEdition')`:
  - Enterprise / Azure SQL (EngineEdition = 3 or 5): `ALTER INDEX ... REBUILD WITH (ONLINE = ON)`
  - Standard / other: `ALTER INDEX ... REBUILD WITH (ONLINE = OFF)` and prints a warning that this blocks concurrent writes
- `@MaxDop` parameter (default 0 = system default) for operator control
- Prints before/after fragmentation %, ghost record count, page count, and elapsed time
- Includes `UPDATE STATISTICS` after rebuild

**Required permissions:** `ALTER` on the table. Enterprise edition for online rebuild.

**Impact:**
- Enterprise/Azure SQL: online, brief schema lock at start and end
- Standard: **offline — blocks all reads and writes on the table for the duration.** Operators should coordinate with publisher downtime or low-traffic windows.

#### 1d. `partition_switch_cleanup.sql`

**Purpose:** Instant physical cleanup of a fully drained outbox table via metadata-only `SWITCH` operation. Emergency use after major outages where millions of rows accumulated and then drained, leaving the table physically bloated with ghost records and empty pages.

**Behavior:**
- **Pre-condition check:** Asserts the Outbox table has 0 rows. Aborts with a clear error message if rows exist.
- Creates a staging table with identical schema (columns, data types, computed column, constraints) matching the Outbox table
- Creates matching indexes on the staging table (required for SWITCH compatibility)
- `ALTER TABLE dbo.Outbox SWITCH TO dbo.Outbox_SwitchStaging` — instant metadata operation, no data movement
- `TRUNCATE TABLE dbo.Outbox_SwitchStaging` — instant, no ghost records, no transaction log bloat
- Drops the staging table
- Reseeds the IDENTITY on the Outbox table to continue from the last used value (queries `IDENT_CURRENT` before the switch)
- Prints confirmation with space reclaimed

**Pre-conditions:**
- Outbox table MUST be fully drained (0 rows)
- All publishers SHOULD be stopped (no concurrent inserts during the switch)

**Required permissions:** `ALTER` on the table, `CREATE TABLE` in the schema

**Impact:** Instant. No data movement. No ghost records. But requires exclusive access (no concurrent inserts).

### 2. Runbook Section

Added to `docs/production-runbook.md` as a new section before "Monitoring & Alerting Recommendations" (after the Emergency Procedures section).

**Section: SQL Server Index Maintenance**

Sub-sections:

#### 2a. When to Run Maintenance

Decision table:

| Scenario | Script | Urgency |
|----------|--------|---------|
| Routine preventive maintenance | `check_ghost_records.sql` then `reorganize_indexes.sql` | Weekly |
| Post broker outage (< 1 hour) | `check_ghost_records.sql` | After drain completes — usually no action needed |
| Post broker outage (> 1 hour) | `check_ghost_records.sql` then `reorganize_indexes.sql` | After drain completes |
| Post major outage (millions of rows drained) | `check_ghost_records.sql` then `rebuild_indexes.sql` | After drain completes |
| Table physically bloated, fully drained | `partition_switch_cleanup.sql` | Emergency |
| `outbox.poll.duration` p99 increasing with low pending count | `check_ghost_records.sql` to diagnose | Investigate immediately |

Cross-reference to FS-12 (Outbox Table Growing Unbounded) as the trigger scenario.

#### 2b. Monitoring Ghost Records

- How to run the check script and interpret each recommendation level
- Connection between ghost records and observable symptoms:
  - `outbox.poll.duration` p99 increasing
  - `outbox.messages.pending` gauge appears correct but poll returns fewer rows than expected
  - `sys.dm_db_index_physical_stats` shows high `ghost_record_count` relative to `record_count`
- Suggested alert: if `outbox.poll.duration` p99 > 500ms AND `outbox.messages.pending` < 1000, check ghost records

#### 2c. Routine Maintenance: REORGANIZE

- When: weekly, or check script shows fragmentation 10-30%
- Steps: run check script, review output, run reorganize, verify with check script again
- Expected duration: seconds to low minutes depending on table size
- Impact: online, no blocking, safe during active publishing

#### 2d. Deep Maintenance: REBUILD

- When: monthly, fragmentation > 30%, or post-major-outage
- Steps: run check script, run rebuild, verify
- Enterprise vs Standard edition behavior (online vs offline)
- Warning for Standard edition: coordinate with publisher downtime or low-traffic window
- Expected duration: minutes for typical table sizes, longer for very large tables

#### 2e. Emergency: Partition Switch Cleanup

- When: table physically bloated after draining millions of rows post-outage
- Pre-conditions: outbox fully drained (0 rows), all publishers stopped
- Steps: stop publishers → verify table empty → run switch script → verify → restart publishers
- Impact: instant metadata operation, but requires exclusive access

#### 2f. Permissions Required

| Script | Required Permissions |
|--------|---------------------|
| `check_ghost_records.sql` | `VIEW DATABASE STATE` |
| `reorganize_indexes.sql` | `ALTER` on Outbox table |
| `rebuild_indexes.sql` | `ALTER` on Outbox table |
| `partition_switch_cleanup.sql` | `ALTER` on Outbox table, `CREATE TABLE` |

Minimum-privilege guidance: create a dedicated `outbox_maintenance` database role with these permissions.

### 3. Known Limitations Entry

Added to `docs/known-limitations.md` under a new heading after the existing "Partition count changes require publisher downtime" section.

**Heading:** `### SQL Server ghost records from continuous DELETE workload`

Content covers:
- What ghost records are and why the outbox pattern triggers them
- When they matter (prolonged outages, sustained high throughput) vs when they don't (normal operation)
- Pointer to maintenance scripts and runbook section
- Note that PostgreSQL is not affected (VACUUM handles this natively)
