CREATE TABLE IF NOT EXISTS legal_risk_cases (
 workspace_id TEXT NOT NULL, case_id TEXT NOT NULL, project_id TEXT NOT NULL,
 provenance_record_id TEXT NOT NULL, expected_provenance_revision INTEGER NOT NULL,
 expected_provenance_digest TEXT NOT NULL, subject_id TEXT NOT NULL,
 subject_reference TEXT NOT NULL, subject_digest TEXT NOT NULL, subject_version INTEGER NOT NULL,
 jurisdictions_json TEXT NOT NULL, policy_version TEXT NOT NULL, snapshot_json TEXT NOT NULL,
 revision INTEGER NOT NULL, status TEXT NOT NULL, evidence TEXT NULL, decision TEXT NULL,
 decision_reason TEXT NULL, message_id TEXT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, case_id), UNIQUE(workspace_id, project_id, subject_id, subject_version, policy_version)
);
CREATE TABLE IF NOT EXISTS legal_risk_findings (
 workspace_id TEXT NOT NULL, case_id TEXT NOT NULL, finding_id TEXT NOT NULL,
 category TEXT NOT NULL, citation TEXT NOT NULL, affected_party TEXT NOT NULL,
 jurisdiction TEXT NOT NULL, severity TEXT NOT NULL, confidence REAL NOT NULL,
 rationale TEXT NOT NULL, evidence TEXT NOT NULL, proposed_mitigation TEXT NOT NULL,
 policy_mandates_human_review INTEGER NOT NULL, resolved INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(workspace_id, case_id, finding_id),
 FOREIGN KEY(workspace_id, case_id) REFERENCES legal_risk_cases(workspace_id, case_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS legal_risk_reviews (
 workspace_id TEXT NOT NULL, case_id TEXT NOT NULL, review_id TEXT NOT NULL,
 reviewer_identity TEXT NOT NULL, reviewer_role TEXT NOT NULL, scope TEXT NOT NULL,
 decision TEXT NOT NULL, rationale TEXT NOT NULL, evidence TEXT NOT NULL, conditions TEXT NULL,
 expires_at_utc TEXT NULL, reviewed_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, case_id, review_id),
 FOREIGN KEY(workspace_id, case_id) REFERENCES legal_risk_cases(workspace_id, case_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS legal_risk_history (
 history_id INTEGER PRIMARY KEY AUTOINCREMENT, workspace_id TEXT NOT NULL, case_id TEXT NOT NULL,
 revision INTEGER NOT NULL, transition TEXT NOT NULL, actor TEXT NOT NULL, reason TEXT NULL,
 payload_json TEXT NOT NULL, occurred_at_utc TEXT NOT NULL,
 UNIQUE(workspace_id, case_id, revision)
);
CREATE TABLE IF NOT EXISTS legal_risk_receipts (
 workspace_id TEXT NOT NULL, request_id TEXT NOT NULL, case_id TEXT NOT NULL,
 operation TEXT NOT NULL, request_fingerprint TEXT NOT NULL, payload_hash TEXT NOT NULL,
 result_revision INTEGER NOT NULL, message_id TEXT NULL, created_at_utc TEXT NOT NULL,
 PRIMARY KEY(workspace_id, request_id)
);
CREATE INDEX IF NOT EXISTS ix_legal_risk_authority ON legal_risk_cases(workspace_id, provenance_record_id, expected_provenance_revision);
CREATE INDEX IF NOT EXISTS ix_legal_risk_findings_case ON legal_risk_findings(workspace_id, case_id);
CREATE INDEX IF NOT EXISTS ix_legal_risk_reviews_case ON legal_risk_reviews(workspace_id, case_id);
CREATE INDEX IF NOT EXISTS ix_legal_risk_history_case ON legal_risk_history(workspace_id, case_id, revision);
