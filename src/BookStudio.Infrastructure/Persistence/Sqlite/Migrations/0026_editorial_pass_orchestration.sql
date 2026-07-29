CREATE TABLE editorial_pass_plans (
    workspace_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    cross_chapter_audit_id TEXT NOT NULL,
    expected_audit_revision INTEGER NOT NULL,
    expected_audit_digest TEXT NOT NULL,
    version INTEGER NOT NULL,
    actor TEXT NOT NULL,
    revision INTEGER NOT NULL,
    status TEXT NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, plan_id)
);

CREATE TABLE editorial_pass_nodes (
    workspace_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    pass_kind TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    dependencies_json TEXT NOT NULL,
    status TEXT NOT NULL,
    attempts INTEGER NOT NULL,
    gate_result TEXT NULL,
    evidence TEXT NULL,
    result TEXT NULL,
    responsible TEXT NULL,
    started_at_utc TEXT NULL,
    completed_at_utc TEXT NULL,
    PRIMARY KEY (workspace_id, plan_id, pass_kind),
    FOREIGN KEY (workspace_id, plan_id)
        REFERENCES editorial_pass_plans(workspace_id, plan_id)
        ON DELETE CASCADE
);

CREATE UNIQUE INDEX ux_editorial_pass_nodes_ordinal
ON editorial_pass_nodes(workspace_id, plan_id, ordinal);

CREATE TABLE editorial_pass_history (
    history_id INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    action TEXT NOT NULL,
    pass_kind TEXT NULL,
    actor TEXT NOT NULL,
    reason TEXT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    UNIQUE (workspace_id, plan_id, revision)
);

CREATE TABLE editorial_pass_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    action TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    resulting_revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, request_id)
);

CREATE INDEX ix_editorial_pass_plans_authority
ON editorial_pass_plans(workspace_id, project_id, cross_chapter_audit_id);

CREATE INDEX ix_editorial_pass_history_plan
ON editorial_pass_history(workspace_id, plan_id, history_id);
