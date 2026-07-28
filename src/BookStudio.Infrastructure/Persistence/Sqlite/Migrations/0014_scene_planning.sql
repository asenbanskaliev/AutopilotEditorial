CREATE TABLE IF NOT EXISTS scene_plans (
  workspace_id TEXT NOT NULL,
  scene_plan_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  book_plan_id TEXT NOT NULL,
  book_plan_version INTEGER NOT NULL CHECK(book_plan_version > 0),
  book_plan_approval_message_id TEXT NOT NULL,
  book_plan_content_digest TEXT NOT NULL,
  schema_version TEXT NOT NULL,
  current_version INTEGER NOT NULL CHECK(current_version > 0),
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, scene_plan_id),
  UNIQUE(workspace_id, book_plan_id, book_plan_version)
);

CREATE TABLE IF NOT EXISTS scene_plan_versions (
  workspace_id TEXT NOT NULL,
  scene_plan_id TEXT NOT NULL,
  version INTEGER NOT NULL CHECK(version > 0),
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','PREPARED','COMMITTED','APPROVED')),
  content_json TEXT NOT NULL,
  content_digest TEXT NULL,
  actor TEXT NOT NULL,
  reason TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, scene_plan_id, version, revision)
);

CREATE TABLE IF NOT EXISTS scene_plan_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  scene_plan_id TEXT NOT NULL,
  operation TEXT NOT NULL,
  request_fingerprint TEXT NOT NULL,
  result_version INTEGER NOT NULL,
  result_revision INTEGER NOT NULL,
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_scene_plans_project ON scene_plans(workspace_id, project_id);
CREATE INDEX IF NOT EXISTS ix_scene_plan_versions_status ON scene_plan_versions(workspace_id, scene_plan_id, status);