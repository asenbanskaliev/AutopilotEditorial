CREATE TABLE scheduler_jobs (
    job_id TEXT PRIMARY KEY NOT NULL,
    job_type TEXT NOT NULL,
    schema_version TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    priority INTEGER NOT NULL CHECK (priority BETWEEN -1000 AND 1000),
    available_at_utc TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('QUEUED', 'RUNNING', 'FAILED', 'COMPLETED')),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    locked_by TEXT NULL,
    locked_until_utc TEXT NULL,
    last_error TEXT NULL,
    completed_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    CHECK (
        (status = 'RUNNING' AND locked_by IS NOT NULL AND locked_until_utc IS NOT NULL)
        OR
        (status <> 'RUNNING' AND locked_by IS NULL AND locked_until_utc IS NULL)
    )
);

CREATE INDEX ix_scheduler_jobs_claim
    ON scheduler_jobs(status, priority DESC, available_at_utc, locked_until_utc, created_at_utc, job_id);
