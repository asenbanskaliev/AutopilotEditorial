CREATE TABLE IF NOT EXISTS book_plans (
  workspace_id TEXT NOT NULL,
  plan_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  specification_id TEXT NOT NULL,
  specification_version INTEGER NOT NULL CHECK(specification_version > 0),
  specification_approval_message_id TEXT NOT NULL,
  schema_version TEXT NOT NULL,
  current_version INTEGER NOT NULL CHECK(current_version > 0),
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, plan_id),
  UNIQUE(workspace_id, specification_id, specification_version)
);

CREATE TABLE IF NOT EXISTS book_plan_versions (
  workspace_id TEXT NOT NULL,
  plan_id TEXT NOT NULL,
  version INTEGER NOT NULL CHECK(version > 0),
  revision INTEGER NOT NULL CHECK(revision > 0),
  status TEXT NOT NULL CHECK(status IN ('DRAFT','PREPARED','COMMITTED','APPROVED')),
  content_json TEXT NOT NULL,
  content_digest TEXT NULL,
  actor TEXT NOT NULL,
  reason TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, plan_id, version, revision),
  FOREIGN KEY(workspace_id, plan_id) REFERENCES book_plans(workspace_id, plan_id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS book_plan_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  plan_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','REVISE','PREPARE','COMMIT','APPROVE','NEXT_VERSION')),
  request_fingerprint TEXT NOT NULL,
  result_version INTEGER NOT NULL,
  result_revision INTEGER NOT NULL,
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_book_plans_project ON book_plans(workspace_id, project_id);
CREATE INDEX IF NOT EXISTS ix_book_plan_versions_status ON book_plan_versions(workspace_id, plan_id, status);
