CREATE TABLE IF NOT EXISTS visual_audits (
    workspace_id TEXT NOT NULL,
    audit_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    expected_asset_revision INTEGER NOT NULL,
    expected_asset_digest TEXT NOT NULL,
    visual_brief_id TEXT NOT NULL,
    expected_visual_brief_revision INTEGER NOT NULL,
    expected_visual_brief_digest TEXT NOT NULL,
    adapter_request_id TEXT NULL,
    adapter_evidence_digest TEXT NULL,
    policy_id TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    requested_checks_json TEXT NOT NULL,
    outcome TEXT NOT NULL,
    status TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, audit_id),
    UNIQUE (workspace_id, request_fingerprint)
);

CREATE TABLE IF NOT EXISTS visual_audit_checks (
    workspace_id TEXT NOT NULL,
    audit_id TEXT NOT NULL,
    check_id TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    check_kind TEXT NOT NULL,
    outcome TEXT NOT NULL,
    severity TEXT NOT NULL,
    confidence TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    finding_code TEXT NULL,
    repair_recommendation TEXT NULL,
    provider_id TEXT NOT NULL,
    provider_version TEXT NOT NULL,
    completed_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, check_id),
    UNIQUE (workspace_id, audit_id, check_kind),
    FOREIGN KEY (workspace_id, audit_id) REFERENCES visual_audits(workspace_id, audit_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_audit_findings (
    workspace_id TEXT NOT NULL,
    audit_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    check_id TEXT NOT NULL,
    finding_code TEXT NOT NULL,
    severity TEXT NOT NULL,
    summary TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    waivable INTEGER NOT NULL,
    repair_recommendation TEXT NULL,
    PRIMARY KEY (workspace_id, finding_id),
    FOREIGN KEY (workspace_id, audit_id) REFERENCES visual_audits(workspace_id, audit_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_audit_decisions (
    workspace_id TEXT NOT NULL,
    audit_id TEXT NOT NULL,
    decision_id TEXT NOT NULL,
    decision TEXT NOT NULL,
    authority TEXT NOT NULL,
    scope TEXT NOT NULL,
    rationale TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    decided_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, decision_id),
    FOREIGN KEY (workspace_id, audit_id) REFERENCES visual_audits(workspace_id, audit_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_audit_waivers (
    workspace_id TEXT NOT NULL,
    audit_id TEXT NOT NULL,
    waiver_id TEXT NOT NULL,
    finding_ids_json TEXT NOT NULL,
    authority TEXT NOT NULL,
    scope TEXT NOT NULL,
    rationale TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, waiver_id),
    FOREIGN KEY (workspace_id, audit_id) REFERENCES visual_audits(workspace_id, audit_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_audit_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    audit_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS visual_audit_history (
    workspace_id TEXT NOT NULL,
    history_id TEXT NOT NULL,
    audit_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, history_id),
    UNIQUE (workspace_id, audit_id, revision, event_type),
    FOREIGN KEY (workspace_id, audit_id) REFERENCES visual_audits(workspace_id, audit_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_visual_audits_asset_authority ON visual_audits(workspace_id, asset_id, expected_asset_revision, expected_asset_digest);
CREATE INDEX IF NOT EXISTS ix_visual_audits_brief_authority ON visual_audits(workspace_id, visual_brief_id, expected_visual_brief_revision, expected_visual_brief_digest);
CREATE INDEX IF NOT EXISTS ix_visual_audit_checks_audit ON visual_audit_checks(workspace_id, audit_id, check_kind);
CREATE INDEX IF NOT EXISTS ix_visual_audit_history_audit ON visual_audit_history(workspace_id, audit_id, revision);
