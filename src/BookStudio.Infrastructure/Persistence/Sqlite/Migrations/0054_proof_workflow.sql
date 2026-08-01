CREATE TABLE IF NOT EXISTS proof_workflows (
    workspace_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    proof_type TEXT NOT NULL,
    locale TEXT NOT NULL,
    reviewer TEXT NOT NULL,
    supersedes_proof_id TEXT NULL,
    executions_json TEXT NOT NULL,
    findings_json TEXT NOT NULL,
    physical_receipt_json TEXT NULL,
    evidence_digest TEXT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, proof_id)
);

CREATE TABLE IF NOT EXISTS proof_checklist_executions (
    workspace_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    checklist_id TEXT NOT NULL,
    checklist_version TEXT NOT NULL,
    input_digest TEXT NOT NULL,
    output_digest TEXT NOT NULL,
    executed_at_utc TEXT NOT NULL,
    execution_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, proof_id, revision, checklist_id, checklist_version)
);

CREATE TABLE IF NOT EXISTS proof_findings (
    workspace_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    checklist_id TEXT NOT NULL,
    checklist_version TEXT NOT NULL,
    rule_id TEXT NOT NULL,
    severity TEXT NOT NULL,
    location TEXT NOT NULL,
    annotation_digest TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    status TEXT NOT NULL,
    disposition TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, proof_id, finding_id)
);

CREATE TABLE IF NOT EXISTS proof_physical_receipts (
    workspace_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    provider TEXT NOT NULL,
    order_reference TEXT NOT NULL,
    received_date TEXT NOT NULL,
    inspected_artifact_digest TEXT NOT NULL,
    reviewer_attestation TEXT NOT NULL,
    recorded_at_utc TEXT NOT NULL,
    receipt_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, proof_id, operation_id)
);

CREATE TABLE IF NOT EXISTS proof_decisions (
    workspace_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
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

CREATE TABLE IF NOT EXISTS proof_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS proof_history (
    workspace_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, proof_id, revision)
);

CREATE TABLE IF NOT EXISTS proof_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    proof_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_proof_workflows_project
    ON proof_workflows(workspace_id, project_id, status);

CREATE INDEX IF NOT EXISTS ix_proof_findings_status
    ON proof_findings(workspace_id, proof_id, severity, status);
