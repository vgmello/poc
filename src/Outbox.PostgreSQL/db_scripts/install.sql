-- =============================================================================
-- Outbox Library — PostgreSQL Schema Installation
-- =============================================================================
-- Tables: outbox, outbox_dead_letter, outbox_producers, outbox_partitions
-- Indexes: partial/covering indexes for efficient polling
-- Seeds: 32 partitions
-- =============================================================================

-- ---------------------------------------------------------------------------
-- outbox — primary event buffer
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox
(
    sequence_number    BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    topic_name         VARCHAR(256)   NOT NULL,
    partition_key      VARCHAR(256)   NOT NULL,
    event_type         VARCHAR(256)   NOT NULL,
    headers            VARCHAR(4000)  NULL,
    payload            VARCHAR(4000)  NOT NULL,
    created_at_utc     TIMESTAMPTZ(3) NOT NULL DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    event_datetime_utc TIMESTAMPTZ(3) NOT NULL,
    event_ordinal      SMALLINT       NOT NULL DEFAULT 0,
    leased_until_utc   TIMESTAMPTZ(3) NULL,
    lease_owner        VARCHAR(128)   NULL,
    retry_count        INT            NOT NULL DEFAULT 0,

    CONSTRAINT pk_outbox PRIMARY KEY (sequence_number)
);

-- ---------------------------------------------------------------------------
-- outbox_dead_letter — rows that exceeded the retry threshold
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_dead_letter
(
    dead_letter_seq      BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    sequence_number      BIGINT         NOT NULL,
    topic_name           VARCHAR(256)   NOT NULL,
    partition_key        VARCHAR(256)   NOT NULL,
    event_type           VARCHAR(256)   NOT NULL,
    headers              VARCHAR(4000)  NULL,
    payload              VARCHAR(4000)  NOT NULL,
    created_at_utc       TIMESTAMPTZ(3) NOT NULL,
    retry_count          INT            NOT NULL,
    event_datetime_utc   TIMESTAMPTZ(3) NOT NULL,
    event_ordinal        SMALLINT       NOT NULL DEFAULT 0,
    dead_lettered_at_utc TIMESTAMPTZ(3) NOT NULL DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    last_error           VARCHAR(2000)  NULL,

    CONSTRAINT pk_outbox_dead_letter PRIMARY KEY (dead_letter_seq)
);

-- ---------------------------------------------------------------------------
-- outbox_producers — heartbeat registry for active publisher instances
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_producers
(
    producer_id        VARCHAR(128)   NOT NULL,
    registered_at_utc  TIMESTAMPTZ(3) NOT NULL DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    last_heartbeat_utc TIMESTAMPTZ(3) NOT NULL DEFAULT (clock_timestamp() AT TIME ZONE 'UTC'),
    host_name          VARCHAR(256)   NULL,

    CONSTRAINT pk_outbox_producers PRIMARY KEY (producer_id)
);

-- ---------------------------------------------------------------------------
-- outbox_partitions — partition affinity assignment map
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_partitions
(
    partition_id      INT            NOT NULL,
    owner_producer_id VARCHAR(128)   NULL,
    owned_since_utc   TIMESTAMPTZ(3) NULL,
    grace_expires_utc TIMESTAMPTZ(3) NULL,

    CONSTRAINT pk_outbox_partitions PRIMARY KEY (partition_id)
);

-- ---------------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------------

-- Unleased rows in causal order (partial index keeps it small at steady state)
CREATE INDEX IF NOT EXISTS ix_outbox_unleased
ON outbox (event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, headers, payload, retry_count, created_at_utc)
WHERE leased_until_utc IS NULL;

-- Expired-lease rows (leading column leased_until_utc for efficient range scan)
CREATE INDEX IF NOT EXISTS ix_outbox_lease_expiry
ON outbox (leased_until_utc, event_datetime_utc, event_ordinal)
INCLUDE (sequence_number, topic_name, partition_key, event_type, headers, payload, retry_count, created_at_utc)
WHERE leased_until_utc IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Seed 32 partitions (idempotent)
-- ---------------------------------------------------------------------------
INSERT INTO outbox_partitions (partition_id, owner_producer_id, owned_since_utc, grace_expires_utc)
SELECT g, NULL, NULL, NULL
FROM generate_series(0, 31) AS g
ON CONFLICT (partition_id) DO NOTHING;
