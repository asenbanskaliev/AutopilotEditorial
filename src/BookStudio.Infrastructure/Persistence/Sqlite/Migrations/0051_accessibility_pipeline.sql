CREATE TABLE IF NOT EXISTS accessibility_runs (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    locale TEXT NOT NULL,
    target_profiles_json TEXT NOT NULL,
    evidence_json TEXT NOT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id)
);

CREATE TABLE IF NOT EXISTS accessibility_executions (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    analyzer_id TEXT NOT NULL,
    analyzer_version TEXT NOT NULL,
    rule_profile TEXT NOT NULL,
    input_digest TEXT NOT NULL,
    output_digest TEXT NOT NULL,
    finding_count INTEGER NOT NULL,
    PRIMARY KEY (workspace_id, run_id, analyzer_id, analyzer_version)
);

CREATE TABLE IF NOT EXISTS accessibility_findings (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    rule_id TEXT NOT NULL,
    category TEXT NOT NULL,
    severity TEXT NOT NULL,
    location TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    remediation_status TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id, finding_id)
);

CREATE TABLE IF NOT EXISTS accessibility_reviews (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    review_id TEXT NOT NULL,
    scope TEXT NOT NULL,
    reviewer TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    disposition TEXT NOT NULL,
    completed INTEGER NOT NULL,
    review_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id, review_id)
);

CREATE TABLE IF NOT EXISTS accessibility_waivers (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    waiver_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    approved_by TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    waiver_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id, waiver_id)
);

CREATE TABLE IF NOT EXISTS accessibility_decisions (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
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

CREATE TABLE IF NOT EXISTS accessibility_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS accessibility_history (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id, revision)
);

CREATE TABLE IF NOT EXISTS accessibility_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);
