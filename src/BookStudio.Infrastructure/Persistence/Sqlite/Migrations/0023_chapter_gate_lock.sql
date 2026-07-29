CREATE TABLE IF NOT EXISTS chapter_gates (
  workspace_id TEXT NOT NULL,
  gate_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  chapter_id TEXT NOT NULL,
  expected_version INTEGER NOT NULL CHECK(expected_version > 0),
  expected_digest TEXT NOT NULL,
  findings_json TEXT NOT NULL,
  actor TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('PROPOSED','EVALUATED','LOCKED','REJECTED','REPAIRREQUIRED','REOPENED')),
  decision TEXT NULL CHECK(decision IS NULL OR decision IN ('APPROVE','REJECT','REPAIR')),
  decision_reason TEXT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, gate_id)
);

CREATE TABLE IF NOT EXISTS chapter_gate_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  gate_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','EVALUATE','DECIDE','REOPEN')),
  request_fingerprint TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS chapter_gate_locks (
  workspace_id TEXT NOT NULL,
  chapter_id TEXT NOT NULL,
  gate_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  locked_version INTEGER NOT NULL,
  locked_digest TEXT NOT NULL,
  locked_at_utc TEXT NOT NULL,
  reopened_at_utc TEXT NULL,
  PRIMARY KEY(workspace_id, chapter_id)
);

CREATE INDEX IF NOT EXISTS ix_chapter_gates_chapter ON chapter_gates(workspace_id, project_id, chapter_id, status);