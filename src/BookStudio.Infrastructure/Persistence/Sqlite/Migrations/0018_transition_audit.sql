CREATE TABLE IF NOT EXISTS transition_audits (
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  scope TEXT NOT NULL CHECK(scope IN ('PARAGRAPH','SCENE','CHAPTER')),
  source_json TEXT NOT NULL,
  target_json TEXT NOT NULL,
  rule_set_version TEXT NOT NULL,
  assessments_json TEXT NOT NULL,
  findings_json TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','RUNNING','REVIEWED','CLOSED')),
  closed_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, audit_id),
  UNIQUE(workspace_id, scope, source_json, target_json, rule_set_version)
);

CREATE TABLE IF NOT EXISTS transition_audit_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','START','ASSESS','FIND','DECIDE','REVIEW','CLOSE')),
  request_fingerprint TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_transition_audits_project ON transition_audits(workspace_id, project_id);
CREATE INDEX IF NOT EXISTS ix_transition_audits_status ON transition_audits(workspace_id, status);
