CREATE TABLE human_gate_requests (
    request_id TEXT PRIMARY KEY NOT NULL,
    workflow_id TEXT NOT NULL,
    workflow_version TEXT NOT NULL,
    step_id TEXT NOT NULL,
    job_id TEXT NOT NULL,
    prompt TEXT NOT NULL,
    schema_version TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('OPEN','CLAIMED','APPROVED','REJECTED','EXPIRED','CANCELLED')),
    claimed_by TEXT NULL,
    claim_until_utc TEXT NULL,
    decision TEXT NULL CHECK (decision IS NULL OR decision IN ('APPROVE','REJECT')),
    decision_note TEXT NULL,
    decided_by TEXT NULL,
    decided_at_utc TEXT NULL,
    resume_message_id TEXT NULL UNIQUE,
    created_at_utc TEXT NOT NULL,
    CHECK ((status = 'CLAIMED' AND claimed_by IS NOT NULL AND claim_until_utc IS NOT NULL) OR status <> 'CLAIMED')
);
CREATE INDEX ix_human_gate_due ON human_gate_requests(status, expires_at_utc, claim_until_utc);
