CREATE TABLE IF NOT EXISTS knowledge_entries (
  workspace_id TEXT NOT NULL,
  entry_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  transition_audit_id TEXT NOT NULL,
  transition_closed_message_id TEXT NOT NULL,
  kind TEXT NOT NULL CHECK(kind IN ('FACT','BELIEF','SECRET')),
  subject TEXT NOT NULL,
  object_text TEXT NOT NULL,
  statement TEXT NOT NULL,
  evidence TEXT NOT NULL,
  knowners_json TEXT NOT NULL,
  excluded_json TEXT NOT NULL,
  disclosures_json TEXT NOT NULL,
  valid_from_utc TEXT NOT NULL,
  valid_to_utc TEXT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','ACTIVE','SUPERSEDED','RETRACTED')),
  activation_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, entry_id),
  UNIQUE(workspace_id, transition_audit_id, kind, subject, object_text, statement)
);

CREATE TABLE IF NOT EXISTS knowledge_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  entry_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','ACTIVATE','DISCLOSE','SUPERSEDE','RETRACT')),
  request_fingerprint TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_knowledge_project ON knowledge_entries(workspace_id, project_id);
CREATE INDEX IF NOT EXISTS ix_knowledge_statement ON knowledge_entries(workspace_id, subject, object_text, status);
