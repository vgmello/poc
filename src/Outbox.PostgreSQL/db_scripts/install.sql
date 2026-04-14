-- =============================================================================
-- Outbox Library — PostgreSQL Schema Installation
-- =============================================================================
-- Tables: outbox, outbox_dead_letter, outbox_publishers, outbox_partitions
-- Indexes: partial/covering indexes for efficient polling
-- Seeds: 64 partitions
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
    headers            VARCHAR(2000)  NULL,
    payload            BYTEA          NOT NULL,
    created_at_utc     TIMESTAMPTZ(3) NOT NULL DEFAULT clock_timestamp(),
    event_datetime_utc TIMESTAMPTZ(3) NOT NULL,
    payload_content_type VARCHAR(100) NOT NULL DEFAULT 'application/json',
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
    headers              VARCHAR(2000)  NULL,
    payload              BYTEA          NOT NULL,
    created_at_utc       TIMESTAMPTZ(3) NOT NULL,
    attempt_count        INT            NOT NULL,
    event_datetime_utc   TIMESTAMPTZ(3) NOT NULL,
    payload_content_type VARCHAR(100)   NOT NULL DEFAULT 'application/json',
    dead_lettered_at_utc TIMESTAMPTZ(3) NOT NULL DEFAULT clock_timestamp(),
    last_error           VARCHAR(2000)  NULL,

    CONSTRAINT pk_outbox_dead_letter PRIMARY KEY (dead_letter_seq)
);

-- ---------------------------------------------------------------------------
-- outbox_publishers — heartbeat registry for active publisher instances
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_publishers
(
    outbox_table_name  VARCHAR(256)   NOT NULL,
    publisher_id       VARCHAR(128)   NOT NULL,
    registered_at_utc  TIMESTAMPTZ(3) NOT NULL DEFAULT clock_timestamp(),
    last_heartbeat_utc TIMESTAMPTZ(3) NOT NULL DEFAULT clock_timestamp(),
    host_name          VARCHAR(256)   NULL,

    CONSTRAINT pk_outbox_publishers PRIMARY KEY (outbox_table_name, publisher_id)
);

-- ---------------------------------------------------------------------------
-- outbox_partitions — partition affinity assignment map
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS outbox_partitions
(
    outbox_table_name    VARCHAR(256)   NOT NULL,
    partition_id         INT            NOT NULL,
    owner_publisher_id   VARCHAR(128)   NULL,
    owned_since_utc      TIMESTAMPTZ(3) NULL,
    grace_expires_utc    TIMESTAMPTZ(3) NULL,

    CONSTRAINT pk_outbox_partitions PRIMARY KEY (outbox_table_name, partition_id)
);

-- ---------------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------------

-- Pending rows in sequence order
CREATE INDEX IF NOT EXISTS ix_outbox_pending
ON outbox (sequence_number)
INCLUDE (topic_name, partition_key, event_type, event_datetime_utc, created_at_utc);

-- Dead-letter lookup by original sequence number (used by replay and purge)
CREATE INDEX IF NOT EXISTS ix_outbox_dead_letter_sequence_number
ON outbox_dead_letter (sequence_number);

-- ---------------------------------------------------------------------------
-- Diagnostic views — human-readable headers/payload for text content types
-- ---------------------------------------------------------------------------
CREATE OR REPLACE VIEW vw_outbox AS
SELECT sequence_number, topic_name, partition_key, event_type,
       payload_content_type,
       headers,
       CASE WHEN payload_content_type IN ('application/json', 'text/plain')
            THEN convert_from(payload, 'UTF8')
       END AS payload_text,
       created_at_utc, event_datetime_utc
FROM outbox;

CREATE OR REPLACE VIEW vw_outbox_dead_letter AS
SELECT dead_letter_seq, sequence_number, topic_name, partition_key, event_type,
       payload_content_type,
       headers,
       CASE WHEN payload_content_type IN ('application/json', 'text/plain')
            THEN convert_from(payload, 'UTF8')
       END AS payload_text,
       attempt_count, created_at_utc, event_datetime_utc,
       dead_lettered_at_utc, last_error
FROM outbox_dead_letter;

-- ---------------------------------------------------------------------------
-- Seed 64 partitions (idempotent)
-- ---------------------------------------------------------------------------
INSERT INTO outbox_partitions (outbox_table_name, partition_id, owner_publisher_id, owned_since_utc, grace_expires_utc)
SELECT 'outbox', g, NULL, NULL, NULL
FROM generate_series(0, 63) AS g
ON CONFLICT (outbox_table_name, partition_id) DO NOTHING;
