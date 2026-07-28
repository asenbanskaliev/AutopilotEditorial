CREATE TABLE IF NOT EXISTS scene_coherence_audits (
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  generation_id TEXT NOT NULL,
  scene_approval_message_id TEXT NOT NULL,
  scene_content_digest TEXT NOT NULL,
  scene_plan_id TEXT NOT NULL,
  scene_plan_version INTEGER NOT NULL CHECK(scene_plan_version > 0),
  scene_key TEXT NOT NULL,
  rule_set_version TEXT NOT NULL,
  source_text TEXT NOT NULL,
  entry_state TEXT NOT NULL,
  exit_state TEXT NOT NULL,
  planned_beats_json TEXT NOT NULL,
  beat_assessments_json TEXT NOT NULL,
  causal_links_json TEXT NOT NULL,
  findings_json TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','RUNNING','REVIEWED','CLOSED')),
  closed_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, audit_id),
  UNIQUE(workspace_id, generation_id, scene_plan_id, scene_plan_version, scene_key, rule_set_version)
);

CREATE TABLE IF NOT EXISTS scene_coherence_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  audit_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','START','ASSESS_BEAT','CAUSAL_LINK','FIND','DECIDE','REVIEW','CLOSE')),
  request_fingerprint TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_scene_coherence_generation ON scene_coherence_audits(workspace_id, generation_id);
CREATE INDEX IF NOT EXISTS ix_scene_coherence_status ON scene_coherence_audits(workspace_id, status);
