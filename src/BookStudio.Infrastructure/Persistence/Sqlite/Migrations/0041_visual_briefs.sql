CREATE TABLE IF NOT EXISTS visual_briefs (
 workspace_id TEXT NOT NULL, brief_id TEXT NOT NULL, project_id TEXT NOT NULL,
 legal_risk_case_id TEXT NOT NULL, expected_legal_risk_revision INTEGER NOT NULL,
 expected_legal_risk_digest TEXT NOT NULL, subject_id TEXT NOT NULL,
 subject_reference TEXT NOT NULL, subject_digest TEXT NOT NULL, subject_version INTEGER NOT NULL,
 brief_type TEXT NOT NULL, target_channel TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
 crop_mode TEXT NOT NULL, safe_zone_json TEXT NOT NULL, art_direction TEXT NOT NULL,
 composition TEXT NOT NULL, subject_identity TEXT NOT NULL, continuity_constraints TEXT NOT NULL,
 style TEXT NOT NULL, palette TEXT NOT NULL, typography_intent TEXT NOT NULL,
 accessibility_intent TEXT NOT NULL, prohibited_elements_json TEXT NOT NULL,
 snapshot_json TEXT NOT NULL, revision INTEGER NOT NULL, status TEXT NOT NULL,
 decision_reason TEXT NULL, message_id TEXT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, brief_id),
 UNIQUE(workspace_id, project_id, subject_id, subject_version, brief_type, target_channel)
);

CREATE TABLE IF NOT EXISTS visual_continuity_references (
 workspace_id TEXT NOT NULL, brief_id TEXT NOT NULL, reference_id TEXT NOT NULL,
 kind TEXT NOT NULL, authority_key TEXT NOT NULL, digest TEXT NOT NULL, version INTEGER NOT NULL,
 evidence TEXT NOT NULL, created_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, brief_id, reference_id),
 FOREIGN KEY(workspace_id, brief_id) REFERENCES visual_briefs(workspace_id, brief_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_brief_reviews (
 workspace_id TEXT NOT NULL, brief_id TEXT NOT NULL, review_id TEXT NOT NULL,
 reviewer_identity TEXT NOT NULL, scope TEXT NOT NULL, decision TEXT NOT NULL,
 rationale TEXT NOT NULL, evidence TEXT NOT NULL, blocking_findings_json TEXT NOT NULL,
 reviewed_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, brief_id, review_id),
 FOREIGN KEY(workspace_id, brief_id) REFERENCES visual_briefs(workspace_id, brief_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS visual_brief_history (
 history_id INTEGER PRIMARY KEY AUTOINCREMENT, workspace_id TEXT NOT NULL, brief_id TEXT NOT NULL,
 revision INTEGER NOT NULL, transition TEXT NOT NULL, actor TEXT NOT NULL, reason TEXT NULL,
 payload_json TEXT NOT NULL, occurred_at_utc TEXT NOT NULL,
 UNIQUE(workspace_id, brief_id, revision)
);

CREATE TABLE IF NOT EXISTS visual_brief_receipts (
 workspace_id TEXT NOT NULL, request_id TEXT NOT NULL, brief_id TEXT NOT NULL,
 operation TEXT NOT NULL, request_fingerprint TEXT NOT NULL, payload_hash TEXT NOT NULL,
 result_revision INTEGER NOT NULL, message_id TEXT NULL, created_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, request_id)
);

CREATE INDEX IF NOT EXISTS ix_visual_briefs_authority ON visual_briefs(workspace_id, legal_risk_case_id, expected_legal_risk_revision);
CREATE INDEX IF NOT EXISTS ix_visual_briefs_subject ON visual_briefs(workspace_id, project_id, subject_id, subject_version);
CREATE INDEX IF NOT EXISTS ix_visual_continuity_authority ON visual_continuity_references(workspace_id, kind, authority_key, version);
CREATE INDEX IF NOT EXISTS ix_visual_brief_history ON visual_brief_history(workspace_id, brief_id, revision);
