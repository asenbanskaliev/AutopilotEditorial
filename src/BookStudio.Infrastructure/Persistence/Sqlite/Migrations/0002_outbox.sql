CREATE TABLE outbox_messages (
    message_id TEXT PRIMARY KEY NOT NULL,
    event_type TEXT NOT NULL,
    schema_version TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    available_at_utc TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('PENDING', 'PROCESSING', 'FAILED', 'PROCESSED')),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    locked_by TEXT NULL,
    locked_until_utc TEXT NULL,
    last_error TEXT NULL,
    processed_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    CHECK (
        (status = 'PROCESSING' AND locked_by IS NOT NULL AND locked_until_utc IS NOT NULL)
        OR
        (status <> 'PROCESSING' AND locked_by IS NULL AND locked_until_utc IS NULL)
    )
);

CREATE INDEX ix_outbox_messages_dispatch
    ON outbox_messages(status, available_at_utc, locked_until_utc, created_at_utc);
