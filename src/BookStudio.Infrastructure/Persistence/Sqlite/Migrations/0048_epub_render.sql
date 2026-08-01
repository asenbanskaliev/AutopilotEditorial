CREATE TABLE IF NOT EXISTS epub_renders (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    manuscript_json TEXT NOT NULL,
    profile TEXT NOT NULL,
    metadata_json TEXT NOT NULL,
    package_json TEXT NULL,
    findings_json TEXT NOT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id)
);

CREATE TABLE IF NOT EXISTS epub_render_entries (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    entry_path TEXT NOT NULL,
    media_type TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    length INTEGER NOT NULL,
    compression TEXT NOT NULL,
    entry_order INTEGER NOT NULL,
    PRIMARY KEY (workspace_id, render_id, entry_path)
);

CREATE TABLE IF NOT EXISTS epub_render_findings (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    code TEXT NOT NULL,
    category TEXT NOT NULL,
    severity TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id, finding_id)
);

CREATE TABLE IF NOT EXISTS epub_render_decisions (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    decision TEXT NOT NULL,
    reason TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    actor TEXT NOT NULL,
    revision INTEGER NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS epub_render_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS epub_render_history (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id, revision)
);

CREATE TABLE IF NOT EXISTS epub_render_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_epub_render_entry_order
ON epub_render_entries(workspace_id, render_id, entry_order);
