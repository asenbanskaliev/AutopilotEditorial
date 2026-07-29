CREATE TABLE IF NOT EXISTS cross_chapter_audits (
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  rule_set TEXT NOT NULL,
  chapters_json TEXT NOT NULL,
  findings_json TEXT NOT NULL,
  actor TEXT NOT NULL,
  evidence TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('PROPOSED','EVALUATED','APPROVED','REJECTED','REPAIR_REQUIRED','REOPENED','STALE')),
  decision TEXT NULL CHECK(decision IS NULL OR decision IN ('APPROVE','REJECT','REPAIR')),
  decision_reason TEXT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, audit_id)
);

CREATE TABLE IF NOT EXISTS cross_chapter_audit_history (
  history_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  revision INTEGER NOT NULL,
  status TEXT NOT NULL,
  chapters_json TEXT NOT NULL,
  findings_json TEXT NOT NULL,
  decision TEXT NULL,
  decision_reason TEXT NULL,
  actor TEXT NOT NULL,
  occurred_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS cross_chapter_audit_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','EVALUATE','DECIDE','REOPEN')),
  request_fingerprint TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_cross_chapter_audits_project
  ON cross_chapter_audits(workspace_id, project_id, status);

CREATE INDEX IF NOT EXISTS ix_cross_chapter_audit_history
  ON cross_chapter_audit_history(workspace_id, audit_id, revision);
