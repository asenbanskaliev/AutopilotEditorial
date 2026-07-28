CREATE TABLE IF NOT EXISTS paragraph_coherence_audits (
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  generation_id TEXT NOT NULL,
  scene_approval_message_id TEXT NOT NULL,
  scene_content_digest TEXT NOT NULL,
  rule_set_version TEXT NOT NULL,
  source_text TEXT NOT NULL,
  paragraphs_json TEXT NOT NULL,
  findings_json TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','RUNNING','REVIEWED','CLOSED')),
  closed_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, audit_id)
);

CREATE TABLE IF NOT EXISTS paragraph_coherence_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','START','FIND','DECIDE','REVIEW','CLOSE')),
  request_fingerprint TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_paragraph_coherence_scene ON paragraph_coherence_audits(workspace_id, generation_id);
CREATE INDEX IF NOT EXISTS ix_paragraph_coherence_status ON paragraph_coherence_audits(workspace_id, status);