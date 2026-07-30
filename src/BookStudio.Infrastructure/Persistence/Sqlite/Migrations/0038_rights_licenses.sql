CREATE TABLE rights_license_cases (
    workspace_id TEXT NOT NULL,
    case_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    bibliography_id TEXT NOT NULL,
    expected_bibliography_revision INTEGER NOT NULL,
    expected_bibliography_digest TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    asset_kind TEXT NOT NULL,
    asset_reference TEXT NOT NULL,
    asset_digest TEXT NOT NULL,
    asset_version INTEGER NOT NULL,
    rights_holder TEXT NOT NULL,
    actor TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    revision INTEGER NOT NULL,
    status TEXT NOT NULL,
    scope_json TEXT NULL,
    valid_from_utc TEXT NULL,
    valid_until_utc TEXT NULL,
    restrictions_json TEXT NOT NULL,
    evidence TEXT NULL,
    decision TEXT NULL,
    decision_reason TEXT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, case_id)
);

CREATE TABLE rights_license_history (
    workspace_id TEXT NOT NULL,
    case_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, case_id, revision),
    FOREIGN KEY(workspace_id, case_id) REFERENCES rights_license_cases(workspace_id, case_id) ON DELETE CASCADE
);

CREATE TABLE rights_license_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    case_id TEXT NOT NULL,
    action TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    resulting_revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, request_id)
);

CREATE INDEX ix_rights_license_asset ON rights_license_cases(workspace_id, asset_id, status);
CREATE INDEX ix_rights_license_authority ON rights_license_cases(workspace_id, bibliography_id, expected_bibliography_revision);
