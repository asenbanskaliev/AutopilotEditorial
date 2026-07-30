CREATE TABLE IF NOT EXISTS ai_provenance_records (
 workspace_id TEXT NOT NULL, record_id TEXT NOT NULL, project_id TEXT NOT NULL,
 rights_case_id TEXT NOT NULL, expected_rights_revision INTEGER NOT NULL, expected_rights_digest TEXT NOT NULL,
 asset_id TEXT NOT NULL, asset_digest TEXT NOT NULL, asset_version INTEGER NOT NULL,
 actor TEXT NOT NULL, snapshot_json TEXT NOT NULL, revision INTEGER NOT NULL, status TEXT NOT NULL,
 classification TEXT, provider TEXT, model TEXT, model_version TEXT, generated_at_utc TEXT,
 prompt_reference TEXT, human_transformations_json TEXT NOT NULL DEFAULT '[]', declared_scope TEXT,
 evidence TEXT, disclosures_json TEXT NOT NULL DEFAULT '[]', policy_version TEXT,
 decision TEXT, decision_reason TEXT, message_id TEXT, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, record_id)
);
CREATE TABLE IF NOT EXISTS ai_provenance_history (
 workspace_id TEXT NOT NULL, record_id TEXT NOT NULL, revision INTEGER NOT NULL,
 action TEXT NOT NULL, actor TEXT NOT NULL, reason TEXT, payload_json TEXT NOT NULL,
 occurred_at_utc TEXT NOT NULL, PRIMARY KEY(workspace_id, record_id, revision)
);
CREATE TABLE IF NOT EXISTS ai_provenance_receipts (
 workspace_id TEXT NOT NULL, request_id TEXT NOT NULL, record_id TEXT NOT NULL,
 action TEXT NOT NULL, request_fingerprint TEXT NOT NULL, payload_hash TEXT NOT NULL,
 result_revision INTEGER NOT NULL, message_id TEXT, created_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_ai_provenance_asset ON ai_provenance_records(workspace_id, asset_id, asset_version);
CREATE INDEX IF NOT EXISTS ix_ai_provenance_authority ON ai_provenance_records(workspace_id, rights_case_id, expected_rights_revision);
