CREATE TABLE IF NOT EXISTS ai_provenance_records (
    record_id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    rights_license_case_id TEXT NOT NULL,
    expected_rights_revision INTEGER NOT NULL,
    expected_rights_digest TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    asset_kind TEXT NOT NULL,
    asset_reference TEXT NOT NULL,
    asset_digest TEXT NOT NULL,
    asset_version INTEGER NOT NULL,
    actor TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    revision INTEGER NOT NULL,
    status TEXT NOT NULL,
    classification TEXT NULL,
    provider TEXT NULL,
    model TEXT NULL,
    model_version TEXT NULL,
    prompt_reference TEXT NULL,
    human_transformations TEXT NULL,
    ai_contribution_percent REAL NULL,
    evidence TEXT NULL,
    decision TEXT NULL,
    decision_reason TEXT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE(workspace_id, project_id, asset_id, asset_version)
);

CREATE TABLE IF NOT EXISTS ai_provenance_disclosures (
    disclosure_id TEXT PRIMARY KEY,
    record_id TEXT NOT NULL,
    channel TEXT NOT NULL,
    locale TEXT NOT NULL,
    format TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    text TEXT NOT NULL,
    policy_compliant INTEGER NOT NULL,
    evidence TEXT NOT NULL,
    FOREIGN KEY(record_id) REFERENCES ai_provenance_records(record_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ai_provenance_history (
    history_id INTEGER PRIMARY KEY AUTOINCREMENT,
    record_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    transition TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    UNIQUE(record_id, revision)
);

CREATE TABLE IF NOT EXISTS ai_provenance_receipts (
    request_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    record_id TEXT NOT NULL,
    operation TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    result_revision INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_ai_provenance_authority ON ai_provenance_records(workspace_id, rights_license_case_id, expected_rights_revision);
CREATE INDEX IF NOT EXISTS ix_ai_provenance_asset ON ai_provenance_records(workspace_id, asset_id, asset_version);
CREATE INDEX IF NOT EXISTS ix_ai_provenance_disclosures_record ON ai_provenance_disclosures(record_id);
CREATE INDEX IF NOT EXISTS ix_ai_provenance_history_record ON ai_provenance_history(record_id, revision);
