-- =============================================================================
-- EventHub Outbox Pattern — PostgreSQL
-- =============================================================================
-- Sections:
--   1. Schema: outbox, outbox_dead_letter, outbox_producers, outbox_partitions
--   2. Indexes (partial/covering)
--   3. Publisher queries: unified poll, delete, release, dead-letter
--   4. Producer registration and heartbeat
--   5. Partition registration and rebalance
--   6. Monitoring queries
--   7. Cleanup and maintenance scripts
-- =============================================================================
-- Key differences from SQL Server version:
--   - IDENTITY → GENERATED ALWAYS AS IDENTITY
--   - SYSUTCDATETIME() → clock_timestamp() AT TIME ZONE 'UTC' (statement-level: NOW())
--   - DATEADD(SECOND, n, ts) → ts + INTERVAL 'n seconds' (parameterised: ts + make_interval(secs => n))
--   - DATEDIFF_BIG(MILLISECOND, a, b) → EXTRACT(EPOCH FROM (b - a)) * 1000
--   - ROWLOCK, READPAST → FOR UPDATE SKIP LOCKED
--   - UPDATE...OUTPUT inserted.* → UPDATE...RETURNING *
--   - DELETE...OUTPUT INTO → WITH deleted AS (DELETE...RETURNING *) INSERT INTO...SELECT FROM deleted
--   - MERGE → INSERT...ON CONFLICT...DO UPDATE
--   - TVP (dbo.SequenceNumberList) → unnest(@ids::bigint[]) or ANY(@ids)
--   - TOP (@n) → LIMIT @n
--   - UPDATE TOP (@n) → UPDATE...WHERE ctid IN (SELECT ctid...LIMIT n FOR UPDATE SKIP LOCKED)
--   - NVARCHAR → VARCHAR (PostgreSQL uses UTF-8 natively)
--   - DATETIME2(3) → TIMESTAMPTZ(3) (PostgreSQL stores in UTC, displays per session timezone)
--   - CHECKSUM() → hashtext() (returns int4, stable across sessions)
--   - No dbo schema prefix (uses public schema by default)
--   - No GO batch separator (statements terminated by ;)
-- =============================================================================

-- =============================================================================
-- SECTION 1: SCHEMA
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1a. outbox — primary event buffer
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox
(
    sequence_number   BIGINT GENERATED ALWAYS AS IDENTITY  NOT NULL,
    topic_name        VARCHAR(256)       NOT NULL,
    partition_key     VARCHAR(256)       NOT NULL,
    event_type        VARCHAR(256)       NOT NULL,
    headers           VARCHAR(4000)      NULL,
    payload           VARCHAR(4000)      NOT NULL,
    created_at_utc    TIMESTAMPTZ(3)     NOT NULL  DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    event_datetime_utc TIMESTAMPTZ(3)    NOT NULL,
    event_ordinal     SMALLINT           NOT NULL  DEFAULT 0,
    leased_until_utc  TIMESTAMPTZ(3)     NULL,
    lease_owner       VARCHAR(128)       NULL,
    retry_count       INT                NOT NULL  DEFAULT 0,

    CONSTRAINT pk_outbox PRIMARY KEY (sequence_number)
);

-- ---------------------------------------------------------------------------
-- 1b. outbox_dead_letter — rows that exceeded the retry threshold
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_dead_letter
(
    dead_letter_seq    BIGINT GENERATED ALWAYS AS IDENTITY  NOT NULL,
    sequence_number    BIGINT             NOT NULL,
    topic_name         VARCHAR(256)       NOT NULL,
    partition_key      VARCHAR(256)       NOT NULL,
    event_type         VARCHAR(256)       NOT NULL,
    headers            VARCHAR(4000)      NULL,
    payload            VARCHAR(4000)      NOT NULL,
    created_at_utc     TIMESTAMPTZ(3)     NOT NULL,
    retry_count        INT                NOT NULL,
    event_datetime_utc TIMESTAMPTZ(3)     NOT NULL,
    event_ordinal      SMALLINT           NOT NULL  DEFAULT 0,
    dead_lettered_at_utc TIMESTAMPTZ(3)   NOT NULL  DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    last_error         VARCHAR(2000)      NULL,

    CONSTRAINT pk_outbox_dead_letter PRIMARY KEY (dead_letter_seq)
);

-- ---------------------------------------------------------------------------
-- 1c. outbox_producers — heartbeat registry for active publisher instances
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_producers
(
    producer_id        VARCHAR(128)   NOT NULL,
    registered_at_utc  TIMESTAMPTZ(3) NOT NULL  DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    last_heartbeat_utc TIMESTAMPTZ(3) NOT NULL  DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    host_name          VARCHAR(256)   NULL,

    CONSTRAINT pk_outbox_producers PRIMARY KEY (producer_id)
);

-- ---------------------------------------------------------------------------
-- 1d. outbox_partitions — partition affinity assignment map
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_partitions
(
    partition_id       INT            NOT NULL,
    owner_producer_id  VARCHAR(128)   NULL,
    owned_since_utc    TIMESTAMPTZ(3) NULL,
    grace_expires_utc  TIMESTAMPTZ(3) NULL,

    CONSTRAINT pk_outbox_partitions PRIMARY KEY (partition_id)
);

-- =============================================================================
-- SECTION 2: INDEXES
-- =============================================================================
-- Note: PostgreSQL supports partial indexes natively. INCLUDE columns are
-- supported since PostgreSQL 11. These indexes mirror the SQL Server filtered
-- covering indexes for identical query plan behaviour.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Unified poll (fresh rows arm): unleased rows in causal order.
-- Partial index on leased_until_utc IS NULL keeps the index small at
-- steady state.
-- ---------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS ix_outbox_unleased
ON outbox (event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, headers, payload, retry_count, created_at_utc)
WHERE leased_until_utc IS NULL;

-- ---------------------------------------------------------------------------
-- Unified poll (expired rows arm): rows whose lease has expired.
-- Leading column leased_until_utc allows efficient range scan.
-- ---------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS ix_outbox_lease_expiry
ON outbox (leased_until_utc, event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, headers, payload, retry_count, created_at_utc)
WHERE leased_until_utc IS NOT NULL;

-- =============================================================================
-- SECTION 3: PUBLISHER QUERIES
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 3a. Unified poll — lease both fresh and expired-lease rows in one pass.
--
-- PostgreSQL equivalent of the SQL Server CTE + UPDATE...OUTPUT pattern.
-- Uses a CTE with FOR UPDATE SKIP LOCKED (equivalent of ROWLOCK, READPAST)
-- and UPDATE...RETURNING (equivalent of OUTPUT inserted.*).
--
-- Parameters (supplied via Npgsql):
--   @batch_size             INT
--   @lease_duration_seconds INT
--   @publisher_id           VARCHAR(128)
--   @total_partitions       INT
--   @max_retry_count        INT
--
-- Notes:
--   FOR UPDATE SKIP LOCKED — row-level lock; skip rows locked by others
--   RETURNING              — return updated rows in one round-trip
--   hashtext()             — stable hash function (returns int4)
-- ---------------------------------------------------------------------------
/*
WITH batch AS (
    SELECT o.sequence_number
    FROM outbox o
    INNER JOIN outbox_partitions op
        ON  op.owner_producer_id = @publisher_id
        AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < NOW())
        AND (ABS(hashtext(o.partition_key)) % @total_partitions) = op.partition_id
    WHERE (o.leased_until_utc IS NULL OR o.leased_until_utc < NOW())
      AND o.retry_count < @max_retry_count
    ORDER BY o.event_datetime_utc, o.event_ordinal
    LIMIT @batch_size
    FOR UPDATE OF o SKIP LOCKED
)
UPDATE outbox o
SET    leased_until_utc = NOW() + make_interval(secs => @lease_duration_seconds),
       lease_owner      = @publisher_id,
       retry_count      = CASE WHEN o.leased_until_utc IS NOT NULL
                               THEN o.retry_count + 1
                               ELSE o.retry_count END
FROM   batch b
WHERE  o.sequence_number = b.sequence_number
RETURNING o.sequence_number, o.topic_name, o.partition_key, o.event_type,
          o.headers, o.payload, o.event_datetime_utc, o.event_ordinal,
          o.retry_count;
*/

-- ---------------------------------------------------------------------------
-- 3b. Delete after successful send — array parameter version.
--
-- Uses ANY(@ids) with a bigint[] parameter instead of SQL Server's TVP.
-- The lease_owner guard prevents zombie publishers from deleting re-leased rows.
--
-- Parameters:
--   @published_ids  BIGINT[]      — array of sequence_numbers to delete
--   @publisher_id   VARCHAR(128)  — must match lease_owner
-- ---------------------------------------------------------------------------
/*
DELETE FROM outbox
WHERE  sequence_number = ANY(@published_ids)
  AND  lease_owner = @publisher_id;
*/

-- ---------------------------------------------------------------------------
-- 3c. Release leased rows — return rows to the unleased pool without
--     incrementing retry_count. Used when the circuit breaker is open.
--
-- Parameters:
--   @ids           BIGINT[]      — array of sequence_numbers to release
--   @publisher_id  VARCHAR(128)  — must match lease_owner
-- ---------------------------------------------------------------------------
/*
UPDATE outbox
SET    leased_until_utc = NULL,
       lease_owner      = NULL
WHERE  sequence_number = ANY(@ids)
  AND  lease_owner = @publisher_id;
*/

-- ---------------------------------------------------------------------------
-- 3d. Dead-letter sweep — move poison messages to outbox_dead_letter.
--
-- PostgreSQL does not support DELETE...OUTPUT INTO. Instead, we use a CTE
-- with DELETE...RETURNING and pipe the results into an INSERT.
--
-- Parameters:
--   @max_retry_count  INT
--   @last_error       VARCHAR(2000)
-- ---------------------------------------------------------------------------
/*
WITH dead AS (
    DELETE FROM outbox o
    USING (
        SELECT sequence_number
        FROM outbox
        WHERE retry_count >= @max_retry_count
          AND (leased_until_utc IS NULL OR leased_until_utc < NOW())
        FOR UPDATE SKIP LOCKED
    ) d
    WHERE o.sequence_number = d.sequence_number
    RETURNING o.sequence_number, o.topic_name, o.partition_key, o.event_type,
              o.headers, o.payload, o.created_at_utc, o.retry_count,
              o.event_datetime_utc, o.event_ordinal
)
INSERT INTO outbox_dead_letter
    (sequence_number, topic_name, partition_key, event_type, headers, payload,
     created_at_utc, retry_count, event_datetime_utc, event_ordinal,
     dead_lettered_at_utc, last_error)
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       created_at_utc, retry_count, event_datetime_utc, event_ordinal,
       NOW(), @last_error
FROM dead;
*/

-- ---------------------------------------------------------------------------
-- 3e. Dead-letter single row — inline detection during publish loop.
--
-- Parameters:
--   @sequence_number  BIGINT
--   @publisher_id     VARCHAR(128)
--   @last_error       VARCHAR(2000)
-- ---------------------------------------------------------------------------
/*
WITH dead AS (
    DELETE FROM outbox
    WHERE sequence_number = @sequence_number
      AND lease_owner = @publisher_id
    RETURNING sequence_number, topic_name, partition_key, event_type,
              headers, payload, created_at_utc, retry_count,
              event_datetime_utc, event_ordinal
)
INSERT INTO outbox_dead_letter
    (sequence_number, topic_name, partition_key, event_type, headers, payload,
     created_at_utc, retry_count, event_datetime_utc, event_ordinal,
     dead_lettered_at_utc, last_error)
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       created_at_utc, retry_count, event_datetime_utc, event_ordinal,
       NOW(), @last_error
FROM dead;
*/

-- =============================================================================
-- SECTION 4: PRODUCER REGISTRATION AND HEARTBEAT
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 4a. Register producer on startup (idempotent upsert via ON CONFLICT).
--
-- Parameters:
--   @producer_id  VARCHAR(128)
--   @host_name    VARCHAR(256)
-- ---------------------------------------------------------------------------
/*
INSERT INTO outbox_producers (producer_id, registered_at_utc, last_heartbeat_utc, host_name)
VALUES (@producer_id, NOW(), NOW(), @host_name)
ON CONFLICT (producer_id) DO UPDATE
SET last_heartbeat_utc = NOW(),
    host_name          = EXCLUDED.host_name;
*/

-- ---------------------------------------------------------------------------
-- 4b. Heartbeat renewal — called every heartbeat_interval_seconds.
--
-- Parameters:
--   @producer_id  VARCHAR(128)
-- ---------------------------------------------------------------------------
/*
UPDATE outbox_producers
SET    last_heartbeat_utc = NOW()
WHERE  producer_id = @producer_id;

UPDATE outbox_partitions
SET    grace_expires_utc = NULL
WHERE  owner_producer_id = @producer_id
  AND  grace_expires_utc IS NOT NULL;
*/

-- ---------------------------------------------------------------------------
-- 4c. Graceful unregistration — called on orderly shutdown.
--
-- Parameters:
--   @producer_id  VARCHAR(128)
-- ---------------------------------------------------------------------------
/*
-- Within a single transaction:
BEGIN;

UPDATE outbox_partitions
SET    owner_producer_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
WHERE  owner_producer_id = @producer_id;

DELETE FROM outbox_producers
WHERE  producer_id = @producer_id;

COMMIT;
*/

-- =============================================================================
-- SECTION 5: PARTITION REGISTRATION AND REBALANCE
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 5a. Initialise partition table — run once at deployment time.
--
-- Parameters:
--   @partition_count  INT
-- ---------------------------------------------------------------------------
/*
INSERT INTO outbox_partitions (partition_id, owner_producer_id, owned_since_utc, grace_expires_utc)
SELECT g, NULL, NULL, NULL
FROM generate_series(0, @partition_count - 1) AS g
ON CONFLICT (partition_id) DO NOTHING;
*/

-- ---------------------------------------------------------------------------
-- 5b. Rebalance — claim + release in a single transaction.
--
-- PostgreSQL does not support UPDATE TOP (@n). Instead we use a subquery
-- with LIMIT and FOR UPDATE SKIP LOCKED to achieve the same effect.
--
-- Parameters:
--   @producer_id                   VARCHAR(128)
--   @heartbeat_timeout_seconds     INT
--   @partition_grace_period_seconds INT
-- ---------------------------------------------------------------------------
/*
BEGIN;

-- Step 1: Mark stale producers' partitions as entering grace period
UPDATE outbox_partitions
SET    grace_expires_utc = NOW() + make_interval(secs => @partition_grace_period_seconds)
WHERE  owner_producer_id <> @producer_id
  AND  owner_producer_id IS NOT NULL
  AND  grace_expires_utc IS NULL
  AND  owner_producer_id NOT IN (
           SELECT producer_id
           FROM   outbox_producers
           WHERE  last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)
       );

-- Step 2: Calculate fair share and claim
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_claim AS (
    SELECT op.partition_id
    FROM outbox_partitions op, fair f
    WHERE (op.owner_producer_id IS NULL OR op.grace_expires_utc < NOW())
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE outbox_partitions
SET    owner_producer_id = @producer_id,
       owned_since_utc   = NOW(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  outbox_partitions.partition_id = to_claim.partition_id;

-- Step 3: Release excess above fair share
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_release AS (
    SELECT op.partition_id
    FROM outbox_partitions op, fair f
    WHERE op.owner_producer_id = @producer_id
      AND f.currently_owned > f.fair_share
    ORDER BY op.partition_id DESC
    LIMIT GREATEST(0, (SELECT f.currently_owned - f.fair_share FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE outbox_partitions
SET    owner_producer_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL
FROM   to_release
WHERE  outbox_partitions.partition_id = to_release.partition_id;

COMMIT;
*/

-- ---------------------------------------------------------------------------
-- 5c. Orphan sweep — pick up unowned partitions up to fair share.
--
-- Parameters:
--   @producer_id                VARCHAR(128)
--   @heartbeat_timeout_seconds  INT
-- ---------------------------------------------------------------------------
/*
WITH counts AS (
    SELECT
        (SELECT COUNT(*) FROM outbox_partitions) AS total_partitions,
        (SELECT COUNT(*) FROM outbox_producers
         WHERE last_heartbeat_utc >= NOW() - make_interval(secs => @heartbeat_timeout_seconds)) AS active_producers,
        (SELECT COUNT(*) FROM outbox_partitions
         WHERE owner_producer_id = @producer_id) AS currently_owned
),
fair AS (
    SELECT
        CEIL(total_partitions::float / NULLIF(active_producers, 0))::int AS fair_share,
        currently_owned
    FROM counts
),
to_claim AS (
    SELECT op.partition_id
    FROM outbox_partitions op, fair f
    WHERE op.owner_producer_id IS NULL
      AND f.fair_share - f.currently_owned > 0
    ORDER BY op.partition_id
    LIMIT GREATEST(0, (SELECT f.fair_share - f.currently_owned FROM fair f))
    FOR UPDATE OF op SKIP LOCKED
)
UPDATE outbox_partitions
SET    owner_producer_id = @producer_id,
       owned_since_utc   = NOW(),
       grace_expires_utc = NULL
FROM   to_claim
WHERE  outbox_partitions.partition_id = to_claim.partition_id;
*/

-- =============================================================================
-- SECTION 6: MONITORING QUERIES
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 6a. Outbox depth and lease distribution
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*)                                                                 AS total_rows,
    SUM(CASE WHEN leased_until_utc IS NULL THEN 1 ELSE 0 END)               AS unleased,
    SUM(CASE WHEN leased_until_utc >= NOW() THEN 1 ELSE 0 END)              AS actively_leased,
    SUM(CASE WHEN leased_until_utc IS NOT NULL
              AND leased_until_utc < NOW() THEN 1 ELSE 0 END)               AS expired_leases,
    MIN(created_at_utc)                                                      AS oldest_message,
    EXTRACT(EPOCH FROM (NOW() - MIN(created_at_utc))) * 1000                 AS max_latency_ms,
    MAX(retry_count)                                                         AS max_retry_count
FROM outbox;

-- ---------------------------------------------------------------------------
-- 6b. Per-topic depth
-- ---------------------------------------------------------------------------
SELECT   topic_name, COUNT(*) AS pending
FROM     outbox
GROUP BY topic_name
ORDER BY pending DESC;

-- ---------------------------------------------------------------------------
-- 6c. Active producers and heartbeat ages
-- ---------------------------------------------------------------------------
SELECT
    producer_id,
    host_name,
    registered_at_utc,
    last_heartbeat_utc,
    EXTRACT(EPOCH FROM (NOW() - last_heartbeat_utc)) * 1000 AS heartbeat_age_ms
FROM outbox_producers
ORDER BY last_heartbeat_utc DESC;

-- ---------------------------------------------------------------------------
-- 6d. Partition ownership and status
-- ---------------------------------------------------------------------------
SELECT
    p.partition_id,
    p.owner_producer_id,
    pr.host_name,
    p.owned_since_utc,
    p.grace_expires_utc,
    CASE
        WHEN p.owner_producer_id IS NULL THEN 'UNOWNED'
        WHEN pr.producer_id IS NULL THEN 'ORPHANED'
        WHEN p.grace_expires_utc IS NOT NULL
             AND p.grace_expires_utc > NOW() THEN 'IN_GRACE'
        ELSE 'OWNED'
    END AS status
FROM outbox_partitions p
LEFT JOIN outbox_producers pr
    ON pr.producer_id = p.owner_producer_id
ORDER BY p.partition_id;

-- ---------------------------------------------------------------------------
-- 6e. Dead-letter queue depth
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*)                AS dead_letter_count,
    MIN(dead_lettered_at_utc) AS oldest_dead_letter,
    MAX(retry_count)        AS max_retries
FROM outbox_dead_letter;

-- ---------------------------------------------------------------------------
-- 6f. SLA breach — rows older than 5 minutes
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*) AS sla_breach_count,
    MIN(created_at_utc) AS oldest_sla_breach_message
FROM outbox
WHERE created_at_utc < NOW() - INTERVAL '5 minutes';

-- ---------------------------------------------------------------------------
-- 6g. Index bloat check (PostgreSQL equivalent of fragmentation check)
-- ---------------------------------------------------------------------------
SELECT
    indexrelname AS index_name,
    pg_size_pretty(pg_relation_size(indexrelid)) AS index_size,
    idx_scan AS index_scans,
    idx_tup_read AS tuples_read,
    idx_tup_fetch AS tuples_fetched
FROM pg_stat_user_indexes
WHERE relname = 'outbox'
ORDER BY pg_relation_size(indexrelid) DESC;

-- ---------------------------------------------------------------------------
-- 6h. Per-producer lease activity
-- ---------------------------------------------------------------------------
SELECT
    lease_owner,
    COUNT(*)                                                    AS leased_rows,
    SUM(CASE WHEN leased_until_utc < NOW() THEN 1
             ELSE 0 END)                                        AS expired_rows,
    MIN(leased_until_utc)                                       AS earliest_expiry,
    MAX(leased_until_utc)                                       AS latest_expiry
FROM outbox
WHERE lease_owner IS NOT NULL
GROUP BY lease_owner
ORDER BY leased_rows DESC;

-- =============================================================================
-- SECTION 7: CLEANUP AND MAINTENANCE SCRIPTS
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 7a. Reindex outbox tables — use REINDEX CONCURRENTLY for online operation
-- ---------------------------------------------------------------------------
/*
REINDEX INDEX CONCURRENTLY ix_outbox_unleased;
REINDEX INDEX CONCURRENTLY ix_outbox_lease_expiry;
REINDEX TABLE CONCURRENTLY outbox_dead_letter;
*/

-- ---------------------------------------------------------------------------
-- 7b. VACUUM after heavy delete activity (usually handled by autovacuum)
-- ---------------------------------------------------------------------------
/*
VACUUM (VERBOSE) outbox;
VACUUM (VERBOSE) outbox_dead_letter;
*/

-- ---------------------------------------------------------------------------
-- 7c. Archive dead-letter rows older than 30 days
-- ---------------------------------------------------------------------------
/*
DELETE FROM outbox_dead_letter
WHERE dead_lettered_at_utc < NOW() - INTERVAL '30 days';
*/

-- ---------------------------------------------------------------------------
-- 7d. Force rebalance — clears the producer registry
-- ---------------------------------------------------------------------------
/*
BEGIN;

UPDATE outbox_partitions
SET    owner_producer_id = NULL,
       owned_since_utc   = NULL,
       grace_expires_utc = NULL;

DELETE FROM outbox_producers;

COMMIT;
*/

-- ---------------------------------------------------------------------------
-- 7e. Replay dead-letter rows — move selected rows back to outbox
-- ---------------------------------------------------------------------------
/*
-- @replay_ids: BIGINT[] of original sequence_numbers to replay
BEGIN;

INSERT INTO outbox (topic_name, partition_key, event_type, headers, payload,
                    created_at_utc, event_datetime_utc, event_ordinal,
                    leased_until_utc, lease_owner, retry_count)
SELECT topic_name, partition_key, event_type, headers, payload,
       created_at_utc, event_datetime_utc, event_ordinal,
       NULL, NULL, 0
FROM outbox_dead_letter
WHERE sequence_number = ANY(@replay_ids);

DELETE FROM outbox_dead_letter
WHERE sequence_number = ANY(@replay_ids);

COMMIT;
*/

-- ---------------------------------------------------------------------------
-- 7f. Emergency cleanup — delete all rows for a specific topic
-- ---------------------------------------------------------------------------
/*
DELETE FROM outbox
WHERE topic_name = 'orders';
*/
