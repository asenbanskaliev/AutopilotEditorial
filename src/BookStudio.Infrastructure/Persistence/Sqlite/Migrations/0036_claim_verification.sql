CREATE TABLE claim_verifications (
    workspace_id TEXT NOT NULL,
    verification_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    research_plan_id TEXT NOT NULL,
    expected_research_plan_revision INTEGER NOT NULL,
    expected_research_plan_digest TEXT NOT NULL,
    claim_id TEXT NOT NULL,
    claim_type TEXT NOT NULL,
    location TEXT NOT NULL,
    version INTEGER NOT NULL,
    rule_set TEXT NOT NULL,
    actor TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    revision INTEGER NOT NULL,
    status TEXT NOT NULL,
    decision TEXT NULL,
    decision_reason TEXT NULL,
    expected_research_revision INTEGER NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, verification_id)
);

CREATE TABLE claim_verification_evidence (
    workspace_id TEXT NOT NULL,
    verification_id TEXT NOT NULL,
    evidence_id TEXT NOT NULL,
    disposition TEXT NOT NULL,
    source_type TEXT NOT NULL,
    source_reference TEXT NOT NULL,
    consulted_at_utc TEXT NOT NULL,
    valid_until_utc TEXT NULL,
    quality TEXT NOT NULL,
    coverage TEXT NOT NULL,
    confidence TEXT NOT NULL,
    location TEXT NOT NULL,
    extract_or_summary TEXT NOT NULL,
    reproducibility_data TEXT NOT NULL,
    is_open INTEGER NOT NULL,
    PRIMARY KEY(workspace_id, verification_id, evidence_id),
    FOREIGN KEY(workspace_id, verification_id) REFERENCES claim_verifications(workspace_id, verification_id) ON DELETE CASCADE
);

CREATE TABLE claim_verification_history (
    workspace_id TEXT NOT NULL,
    verification_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, verification_id, revision)
);

CREATE TABLE claim_verification_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    verification_id TEXT NOT NULL,
    action TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    resulting_revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, request_id)
);
