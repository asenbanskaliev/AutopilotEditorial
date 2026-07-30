CREATE TABLE research_plans (
    workspace_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    originality_review_id TEXT NOT NULL,
    expected_originality_revision INTEGER NOT NULL,
    expected_originality_digest TEXT NOT NULL,
    version INTEGER NOT NULL,
    actor TEXT NOT NULL,
    evidence TEXT NOT NULL,
    revision INTEGER NOT NULL,
    status TEXT NOT NULL,
    decision TEXT NULL,
    decision_reason TEXT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, plan_id)
);

CREATE TABLE research_questions (
    workspace_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    question_id TEXT NOT NULL,
    type TEXT NOT NULL,
    priority TEXT NOT NULL,
    location TEXT NOT NULL,
    claim_ids_json TEXT NOT NULL,
    editorial_decision_ids_json TEXT NOT NULL,
    question TEXT NOT NULL,
    source_strategy TEXT NOT NULL,
    quality_criteria TEXT NOT NULL,
    currency_criteria TEXT NOT NULL,
    coverage_criteria TEXT NOT NULL,
    expected_evidence TEXT NOT NULL,
    dependency_question_ids_json TEXT NOT NULL,
    owner TEXT NULL,
    status TEXT NOT NULL,
    attempts INTEGER NOT NULL,
    PRIMARY KEY(workspace_id, plan_id, question_id),
    FOREIGN KEY(workspace_id, plan_id) REFERENCES research_plans(workspace_id, plan_id) ON DELETE CASCADE
);

CREATE TABLE research_plan_history (
    workspace_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, plan_id, revision)
);

CREATE TABLE research_plan_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    action TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    resulting_revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, request_id)
);
