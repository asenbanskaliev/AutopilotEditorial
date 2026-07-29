CREATE TABLE themes_pacing_reviews (
    workspace_id TEXT NOT NULL,
    review_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    editorial_plan_id TEXT NOT NULL,
    dialogue_review_id TEXT NOT NULL,
    expected_dialogue_revision INTEGER NOT NULL,
    expected_dialogue_digest TEXT NOT NULL,
    version INTEGER NOT NULL,
    rule_set TEXT NOT NULL,
    actor TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    revision INTEGER NOT NULL,
    status TEXT NOT NULL,
    decision TEXT NULL,
    decision_reason TEXT NULL,
    expected_repair_revision INTEGER NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, review_id)
);
CREATE TABLE themes_pacing_findings (
    workspace_id TEXT NOT NULL,
    review_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    area TEXT NOT NULL,
    severity TEXT NOT NULL,
    rule TEXT NOT NULL,
    location TEXT NOT NULL,
    chapter_numbers_json TEXT NOT NULL,
    scene_ids_json TEXT NOT NULL,
    beat_ids_json TEXT NOT NULL,
    spans_json TEXT NOT NULL,
    evidence TEXT NOT NULL,
    is_open INTEGER NOT NULL,
    PRIMARY KEY (workspace_id, review_id, finding_id),
    FOREIGN KEY (workspace_id, review_id) REFERENCES themes_pacing_reviews(workspace_id, review_id) ON DELETE CASCADE
);
CREATE TABLE themes_pacing_history (
    history_id INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id TEXT NOT NULL,
    review_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    UNIQUE (workspace_id, review_id, revision)
);
CREATE TABLE themes_pacing_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    review_id TEXT NOT NULL,
    action TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    resulting_revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, request_id)
);
CREATE INDEX ix_themes_pacing_reviews_authority ON themes_pacing_reviews(workspace_id, project_id, editorial_plan_id, dialogue_review_id);
CREATE INDEX ix_themes_pacing_history_review ON themes_pacing_history(workspace_id, review_id, history_id);
