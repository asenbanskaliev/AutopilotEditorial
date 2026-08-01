CREATE TABLE IF NOT EXISTS technical_preflight_runs (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    production_artifact_digest TEXT NOT NULL,
    target_profile TEXT NOT NULL,
    locale TEXT NOT NULL,
    rule_profile TEXT NOT NULL,
    executions_json TEXT NOT NULL,
    findings_json TEXT NOT NULL,
    waivers_json TEXT NOT NULL,
    evidence_digest TEXT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id)
);

CREATE TABLE IF NOT EXISTS technical_preflight_executions (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    checker_id TEXT NOT NULL,
    checker_version TEXT NOT NULL,
    rule_profile TEXT NOT NULL,
    input_digest TEXT NOT NULL,
    output_digest TEXT NOT NULL,
    execution_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id, checker_id, checker_version)
);

CREATE TABLE IF NOT EXISTS technical_preflight_findings (
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    code TEXT NOT NULL,
    severity TEXT NOT NULL,
    location TEXT NOT NULL,
    rule_id TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    remediation_status TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, run_id, finding_id)
);

CREATE TABLE IF NOT EXISTS technical_preflight_waivers (
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

CREATE TABLE IF NOT EXISTS technical_preflight_decisions (
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

CREATE TABLE IF NOT EXISTS technical_preflight_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS technical_preflight_history (
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

CREATE TABLE IF NOT EXISTS technical_preflight_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);
