CREATE TABLE discovery_sessions (
  workspace_id TEXT NOT NULL,
  session_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  schema_version TEXT NOT NULL,
  request_fingerprint TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('OPEN','COMPLETED','CANCELLED')),
  version INTEGER NOT NULL,
  completion_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, session_id)
);
CREATE TABLE discovery_questions (
  workspace_id TEXT NOT NULL, session_id TEXT NOT NULL, question_key TEXT NOT NULL,
  question_order INTEGER NOT NULL, question_type TEXT NOT NULL, required INTEGER NOT NULL,
  prompt TEXT NOT NULL, PRIMARY KEY(workspace_id,session_id,question_key),
  FOREIGN KEY(workspace_id,session_id) REFERENCES discovery_sessions(workspace_id,session_id)
);
CREATE TABLE discovery_answers (
  workspace_id TEXT NOT NULL, session_id TEXT NOT NULL, question_key TEXT NOT NULL,
  answer_version INTEGER NOT NULL, answer_json TEXT NOT NULL, actor TEXT NOT NULL, answered_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id,session_id,question_key,answer_version)
);
CREATE TABLE discovery_decisions (
  workspace_id TEXT NOT NULL, session_id TEXT NOT NULL, decision_key TEXT NOT NULL,
  selected_option TEXT NOT NULL, rationale TEXT NOT NULL, actor TEXT NOT NULL,
  evidence_reference TEXT NULL, decided_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id,session_id,decision_key)
);
CREATE TABLE discovery_open_items (
  workspace_id TEXT NOT NULL, session_id TEXT NOT NULL, item_key TEXT NOT NULL,
  description TEXT NOT NULL, required INTEGER NOT NULL, resolved INTEGER NOT NULL,
  actor TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id,session_id,item_key)
);
CREATE TABLE discovery_requests (
  request_id TEXT PRIMARY KEY NOT NULL, workspace_id TEXT NOT NULL, session_id TEXT NOT NULL,
  operation TEXT NOT NULL, request_fingerprint TEXT NOT NULL, created_at_utc TEXT NOT NULL
);
CREATE INDEX ix_discovery_status ON discovery_sessions(workspace_id,status,updated_at_utc);
