CREATE TABLE IF NOT EXISTS repair_patch_targets (
  workspace_id TEXT NOT NULL,
  artifact_id TEXT NOT NULL,
  version INTEGER NOT NULL CHECK(version > 0),
  digest TEXT NOT NULL,
  content_json TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, artifact_id)
);

CREATE TABLE IF NOT EXISTS repair_patches (
  workspace_id TEXT NOT NULL,
  patch_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  artifact_id TEXT NOT NULL,
  expected_version INTEGER NOT NULL,
  expected_digest TEXT NOT NULL,
  scope TEXT NOT NULL,
  operations_json TEXT NOT NULL,
  reason TEXT NOT NULL,
  evidence TEXT NOT NULL,
  authority_type TEXT NOT NULL CHECK(authority_type IN ('FINDING','AUDIT')),
  authority_id TEXT NOT NULL,
  actor TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('PROPOSED','VALIDATED','APPLIED','REJECTED','STALE')),
  result_digest TEXT NULL,
  result_version INTEGER NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, patch_id)
);

CREATE TABLE IF NOT EXISTS repair_patch_history (
  history_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  patch_id TEXT NOT NULL,
  revision INTEGER NOT NULL,
  status TEXT NOT NULL,
  artifact_version INTEGER NOT NULL,
  artifact_digest TEXT NOT NULL,
  content_json TEXT NOT NULL,
  actor TEXT NOT NULL,
  occurred_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS repair_patch_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  patch_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('PROPOSE','VALIDATE','APPLY','REJECT')),
  request_fingerprint TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_repair_patches_target ON repair_patches(workspace_id, artifact_id, status);
