CREATE TABLE IF NOT EXISTS timeline_events (
  workspace_id TEXT NOT NULL,
  event_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  knowledge_entry_id TEXT NOT NULL,
  transition_audit_id TEXT NOT NULL,
  transition_closed_message_id TEXT NOT NULL,
  event_key TEXT NOT NULL,
  narrative_order INTEGER NOT NULL CHECK(narrative_order >= 0),
  occurs_at_utc TEXT NOT NULL,
  depends_on_json TEXT NOT NULL,
  summary TEXT NOT NULL,
  actor TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','ACTIVE')),
  activation_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id,event_id),
  UNIQUE(workspace_id,project_id,event_key)
);

CREATE TABLE IF NOT EXISTS plot_threads (
  workspace_id TEXT NOT NULL,
  thread_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  thread_key TEXT NOT NULL,
  title TEXT NOT NULL,
  required_event_ids_json TEXT NOT NULL,
  milestones_json TEXT NOT NULL,
  actor TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('PLANNED','ACTIVE','RESOLVED','ABANDONED')),
  last_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id,thread_id),
  UNIQUE(workspace_id,project_id,thread_key)
);

CREATE TABLE IF NOT EXISTS timeline_plot_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  aggregate_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE_EVENT','ACTIVATE_EVENT','CREATE_THREAD','ADVANCE_THREAD')),
  request_fingerprint TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_timeline_order ON timeline_events(workspace_id,project_id,narrative_order,status);
CREATE INDEX IF NOT EXISTS ix_plot_status ON plot_threads(workspace_id,project_id,status);
