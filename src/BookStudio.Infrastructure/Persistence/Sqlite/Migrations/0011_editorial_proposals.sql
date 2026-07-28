CREATE TABLE IF NOT EXISTS editorial_proposals (
  workspace_id TEXT NOT NULL,
  proposal_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  discovery_session_id TEXT NOT NULL,
  schema_version TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('DRAFT','SUBMITTED','APPROVED','REJECTED')),
  revision INTEGER NOT NULL CHECK(revision > 0),
  decision_actor TEXT NULL,
  decision_reason TEXT NULL,
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, proposal_id),
  UNIQUE(workspace_id, project_id, discovery_session_id)
);

CREATE TABLE IF NOT EXISTS editorial_proposal_revisions (
  workspace_id TEXT NOT NULL,
  proposal_id TEXT NOT NULL,
  revision INTEGER NOT NULL,
  content_json TEXT NOT NULL,
  evidence_json TEXT NOT NULL,
  actor TEXT NOT NULL,
  reason TEXT NOT NULL,
  content_fingerprint TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  PRIMARY KEY(workspace_id, proposal_id, revision),
  FOREIGN KEY(workspace_id, proposal_id) REFERENCES editorial_proposals(workspace_id, proposal_id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS editorial_proposal_requests (
  request_id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  proposal_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','REVISE','SUBMIT','APPROVE','REJECT')),
  request_fingerprint TEXT NOT NULL,
  result_revision INTEGER NOT NULL,
  approval_message_id TEXT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_editorial_proposals_project
  ON editorial_proposals(workspace_id, project_id, status);
