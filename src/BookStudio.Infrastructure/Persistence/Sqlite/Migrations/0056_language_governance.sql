PRAGMA foreign_keys = ON;

CREATE TABLE language_governance_policies (
    workspace_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    ui_language_tag TEXT NOT NULL,
    book_language_tag TEXT NOT NULL,
    locale_profile TEXT NOT NULL,
    policy_revision INTEGER NOT NULL,
    policy_digest TEXT NOT NULL,
    allowed_scopes_json TEXT NOT NULL,
    compiled_contract_json TEXT,
    last_validation_json TEXT,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, policy_id)
);

CREATE UNIQUE INDEX ux_language_governance_project_revision
ON language_governance_policies(workspace_id, project_id, policy_revision);

CREATE TABLE language_governance_findings (
    workspace_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    finding_id TEXT NOT NULL,
    rule_id TEXT NOT NULL,
    severity TEXT NOT NULL,
    start_offset INTEGER,
    length INTEGER,
    expected_language_tag TEXT NOT NULL,
    detected_language_tag TEXT NOT NULL,
    confidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    covered_by_approved_scope INTEGER NOT NULL,
    finding_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, policy_id, revision, finding_id)
);

CREATE TABLE language_governance_decisions (
    workspace_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    decision TEXT NOT NULL,
    reason TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    actor TEXT NOT NULL,
    revision INTEGER NOT NULL,
    decided_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE language_governance_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE language_governance_history (
    workspace_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, policy_id, revision)
);

CREATE TABLE language_governance_outbox (
    message_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    dispatched_at_utc TEXT
);
