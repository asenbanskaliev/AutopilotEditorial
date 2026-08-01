CREATE TABLE IF NOT EXISTS docx_renders (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    locale TEXT NOT NULL,
    template_profile TEXT NOT NULL,
    compatibility_target TEXT NOT NULL,
    artifact_json TEXT NULL,
    findings_json TEXT NOT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id)
);

CREATE TABLE IF NOT EXISTS docx_render_parts (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    part_name TEXT NOT NULL,
    part_order INTEGER NOT NULL,
    content_type TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id, part_name)
);

CREATE TABLE IF NOT EXISTS docx_render_relationships (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    relationship_id TEXT NOT NULL,
    source_part TEXT NOT NULL,
    target TEXT NOT NULL,
    relationship_type TEXT NOT NULL,
    external INTEGER NOT NULL,
    PRIMARY KEY (workspace_id, render_id, relationship_id)
);

CREATE TABLE IF NOT EXISTS docx_render_resources (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    part_name TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    rights_approved INTEGER NOT NULL,
    accessibility_alternative TEXT NULL,
    PRIMARY KEY (workspace_id, render_id, resource_id)
);

CREATE TABLE IF NOT EXISTS docx_render_findings (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    code TEXT NOT NULL,
    severity TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id, finding_id)
);

CREATE TABLE IF NOT EXISTS docx_render_decisions (
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

CREATE TABLE IF NOT EXISTS docx_render_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS docx_render_history (
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

CREATE TABLE IF NOT EXISTS docx_render_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_docx_render_part_order
ON docx_render_parts(workspace_id, render_id, part_order);
