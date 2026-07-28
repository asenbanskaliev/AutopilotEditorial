CREATE TABLE IF NOT EXISTS scene_generations (
  workspace_id TEXT NOT NULL,
  generation_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  scene_plan_id TEXT NOT NULL,
  scene_plan_version INTEGER NOT NULL CHECK(scene_plan_version > 0),
  scene_plan_approval_message_id TEXT NOT NULL,
  scene_plan_content_digest TEXT NOT NULL,
  schema_version TEXT NOT NULL,
  brief_json TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('PLANNED','GENERATING','GENERATED','FAILED','SUBMITTED','APPROVED')),
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, generation_id),
  UNIQUE(workspace_id, scene_plan_id, scene_plan_version, generation_id)
);

CREATE TABLE IF NOT EXISTS scene_generation_attempts (
  workspace_id TEXT NOT NULL,
  generation_id TEXT NOT NULL,
  attempt INTEGER NOT NULL CHECK(attempt > 0),
  status TEXT NOT NULL CHECK(status IN ('RUNNING','GENERATED','FAILED')),
  invocation_json TEXT NOT NULL,
  generated_text TEXT NULL,
  content_digest TEXT NULL,
  acceptance_evidence_json TEXT NOT NULL,
  error_class TEXT NULL,
  error_text TEXT NULL,
  retryable INTEGER NULL,
  actor TEXT NOT NULL,
  started_at_utc TEXT NOT NULL,
  finished_at_utc TEXT NULL,
  PRIMARY KEY(workspace_id, generation_id, attempt),
  FOREIGN KEY(workspace_id, generation_id) REFERENCES scene_generations(workspace_id, generation_id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS scene_generation_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  generation_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','START','COMPLETE','FAIL','SUBMIT','APPROVE')),
  request_fingerprint TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  result_attempt INTEGER NULL,
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_scene_generations_plan ON scene_generations(workspace_id, scene_plan_id, scene_plan_version);
CREATE INDEX IF NOT EXISTS ix_scene_generations_status ON scene_generations(workspace_id, status);
