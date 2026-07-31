CREATE TABLE IF NOT EXISTS visual_accessibility_cases (
    workspace_id TEXT NOT NULL,
    accessibility_case_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    channel TEXT NOT NULL,
    locale TEXT NOT NULL,
    visuals_json TEXT NOT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, accessibility_case_id)
);

CREATE TABLE IF NOT EXISTS visual_accessibility_assessments (
    workspace_id TEXT NOT NULL,
    accessibility_case_id TEXT NOT NULL,
    assessment_id TEXT NOT NULL,
    visual_use_id TEXT NOT NULL,
    assessment_kind TEXT NOT NULL,
    outcome TEXT NOT NULL,
    assessment_json TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, accessibility_case_id, assessment_id),
    FOREIGN KEY (workspace_id, accessibility_case_id)
        REFERENCES visual_accessibility_cases(workspace_id, accessibility_case_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_accessibility_findings (
    workspace_id TEXT NOT NULL,
    accessibility_case_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    visual_use_id TEXT NOT NULL,
    code TEXT NOT NULL,
    severity TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, accessibility_case_id, finding_id),
    FOREIGN KEY (workspace_id, accessibility_case_id)
        REFERENCES visual_accessibility_cases(workspace_id, accessibility_case_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_accessibility_decisions (
    workspace_id TEXT NOT NULL,
    accessibility_case_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    decision TEXT NOT NULL,
    reason TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    actor TEXT NOT NULL,
    revision INTEGER NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS visual_accessibility_history (
    workspace_id TEXT NOT NULL,
    accessibility_case_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, accessibility_case_id, revision)
);

CREATE TABLE IF NOT EXISTS visual_accessibility_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    accessibility_case_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS visual_accessibility_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    accessibility_case_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    UNIQUE (workspace_id, accessibility_case_id, revision)
);

CREATE INDEX IF NOT EXISTS ix_visual_accessibility_history_latest
    ON visual_accessibility_history(workspace_id, accessibility_case_id, revision DESC);
CREATE INDEX IF NOT EXISTS ix_visual_accessibility_assessments_case
    ON visual_accessibility_assessments(workspace_id, accessibility_case_id);
CREATE INDEX IF NOT EXISTS ix_visual_accessibility_findings_case
    ON visual_accessibility_findings(workspace_id, accessibility_case_id);