CREATE TABLE dead_letters (
    dead_letter_id TEXT PRIMARY KEY NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('SCHEDULER_JOB','OUTBOX_MESSAGE')),
    source_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    original_schema_version TEXT NOT NULL,
    original_payload_json TEXT NOT NULL,
    attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
    failure_class TEXT NOT NULL CHECK (failure_class IN ('TRANSIENT_EXHAUSTED','PERMANENT','CONTRACT_VIOLATION','SECURITY_VIOLATION','UNKNOWN')),
    error TEXT NOT NULL,
    failure_fingerprint TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('QUARANTINED','READY_FOR_RETRY','REQUEUED','DISCARDED')),
    replacement_schema_version TEXT NULL,
    replacement_payload_json TEXT NULL,
    last_actor TEXT NULL,
    last_reason TEXT NULL,
    recovery_message_id TEXT NULL,
    captured_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    CHECK (
        (status = 'QUARANTINED' AND replacement_schema_version IS NULL AND replacement_payload_json IS NULL AND recovery_message_id IS NULL)
        OR
        (status = 'READY_FOR_RETRY' AND replacement_schema_version IS NOT NULL AND replacement_payload_json IS NOT NULL AND recovery_message_id IS NULL)
        OR
        (status = 'REQUEUED' AND replacement_schema_version IS NOT NULL AND replacement_payload_json IS NOT NULL AND recovery_message_id IS NOT NULL)
        OR
        (status = 'DISCARDED' AND recovery_message_id IS NULL)
    )
);

CREATE TABLE dead_letter_requests (
    request_id TEXT PRIMARY KEY NOT NULL,
    dead_letter_id TEXT NOT NULL,
    operation TEXT NOT NULL CHECK (operation IN ('REPAIR','REQUEUE','DISCARD')),
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    result_status TEXT NOT NULL,
    recovery_message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (dead_letter_id) REFERENCES dead_letters(dead_letter_id)
);

CREATE INDEX ix_dead_letters_status
    ON dead_letters(status, failure_class, captured_at_utc, dead_letter_id);

CREATE INDEX ix_dead_letter_requests_record
    ON dead_letter_requests(dead_letter_id, created_at_utc, request_id);
