CREATE TABLE IF NOT EXISTS memory_deltas (
  workspace_id TEXT NOT NULL,
  delta_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  chapter_id TEXT NOT NULL,
  gate_id TEXT NOT NULL,
  locked_version INTEGER NOT NULL CHECK(locked_version > 0),
  locked_digest TEXT NOT NULL,
  entries_json TEXT NOT NULL,
  evidence TEXT NOT NULL,
  actor TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('PROPOSED','VALIDATED','COMMITTED','REJECTED','STALE')),
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, delta_id)
);

CREATE TABLE IF NOT EXISTS memory_delta_history (
  history_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  delta_id TEXT NOT NULL,
  revision INTEGER NOT NULL,
  status TEXT NOT NULL,
  snapshot_json TEXT NOT NULL,
  actor TEXT NOT NULL,
  occurred_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_projection_entries (
  workspace_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  chapter_id TEXT NOT NULL,
  projection TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  digest TEXT NOT NULL,
  source_delta_id TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, projection, entity_id)
);

CREATE TABLE IF NOT EXISTS memory_delta_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  delta_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('PROPOSE','VALIDATE','COMMIT','REJECT')),
  request_fingerprint TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_memory_deltas_chapter ON memory_deltas(workspace_id, project_id, chapter_id, status);
CREATE INDEX IF NOT EXISTS ix_memory_projection_source ON memory_projection_entries(workspace_id, source_delta_id);