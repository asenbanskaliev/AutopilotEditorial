CREATE TABLE controlled_executions (
    execution_id TEXT PRIMARY KEY NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('RUNNABLE','RUNNING','PAUSED','CANCELLED')),
    version INTEGER NOT NULL DEFAULT 0 CHECK (version >= 0),
    last_actor TEXT NULL,
    last_reason TEXT NULL,
    active_job_id TEXT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE execution_control_receipts (
    request_id TEXT PRIMARY KEY NOT NULL,
    execution_id TEXT NOT NULL,
    action TEXT NOT NULL CHECK (action IN ('PAUSE','RESUME','CANCEL')),
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    resulting_status TEXT NOT NULL CHECK (resulting_status IN ('RUNNABLE','RUNNING','PAUSED','CANCELLED')),
    resulting_version INTEGER NOT NULL CHECK (resulting_version >= 0),
    control_message_id TEXT NOT NULL UNIQUE,
    applied_at_utc TEXT NOT NULL,
    FOREIGN KEY (execution_id) REFERENCES controlled_executions(execution_id)
);

CREATE INDEX ix_execution_control_receipts_execution
    ON execution_control_receipts(execution_id, applied_at_utc, request_id);
