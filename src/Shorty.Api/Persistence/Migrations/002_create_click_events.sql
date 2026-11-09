-- Click events, written by the analytics worker (src/Shorty.Analytics), read by the stats endpoint.
-- Partitioned by month so old data can be dropped as a whole partition instead of with a slow DELETE, and so a query for
-- recent days reads only recent partitions. The worker creates the monthly partitions ahead of time.
-- The primary key contains the partition column (PostgreSQL requires that) and is what makes redelivery harmless:
-- a click stored twice is the same (event_id, clicked_at) and the second insert does nothing.
CREATE TABLE click_events (
    event_id      uuid        NOT NULL,
    code          text        NOT NULL,
    clicked_at    timestamptz NOT NULL,
    referrer_host text        NULL,
    PRIMARY KEY (event_id, clicked_at)
) PARTITION BY RANGE (clicked_at);

-- A safety net so ingestion never fails because a month's partition is missing. It must stay (nearly) empty: PostgreSQL
-- will not create a month's partition while the default partition holds rows that belong to it.
CREATE TABLE click_events_default PARTITION OF click_events DEFAULT;

CREATE INDEX ix_click_events_code_clicked_at ON click_events (code, clicked_at);
