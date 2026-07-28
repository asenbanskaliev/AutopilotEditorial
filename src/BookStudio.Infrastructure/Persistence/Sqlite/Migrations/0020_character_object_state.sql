CREATE TABLE IF NOT EXISTS narrative_states (
  workspace_id TEXT NOT NULL,
  state_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  knowledge_entry_id TEXT NOT NULL,
  transition_audit_id TEXT NOT NULL,
  transition_closed_message_id TEXT NOT NULL,
  entity_kind TEXT NOT NULL CHECK(entity_kind IN ('CHARACTER','OBJECT')),
  entity_key TEXT NOT NULL,
  dimension TEXT NOT NULL,
  value_text TEXT NOT NULL,
  location_text TEXT NULL,
  holder_text TEXT NULL,
  object_type TEXT NULL,
  available INTEGER NOT NULL CHECK(available IN (0,1)),
  transfers_json TEXT NOT NULL,
  valid_from_utc TEXT NOT NULL,
  valid_to_utc TEXT NULL,
  actor TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','ACTIVE','SUPERSEDED','RETRACTED')),
  activation_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, state_id)
);

CREATE TABLE IF NOT EXISTS narrative_state_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  state_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','ACTIVATE','TRANSFER','SUPERSEDE','RETRACT')),
  request_fingerprint TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_narrative_state_entity ON narrative_states(workspace_id, project_id, entity_kind, entity_key, dimension, status);