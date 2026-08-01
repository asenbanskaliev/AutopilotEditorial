CREATE TABLE IF NOT EXISTS print_pdf_renders (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    geometry_json TEXT NOT NULL,
    paper_json TEXT NOT NULL,
    metadata_json TEXT NOT NULL,
    artifact_json TEXT NULL,
    findings_json TEXT NOT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id)
);

CREATE TABLE IF NOT EXISTS print_pdf_pages (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    page_id TEXT NOT NULL,
    page_number INTEGER NOT NULL,
    page_kind TEXT NOT NULL,
    page_side TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    boxes_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id, page_id)
);

CREATE TABLE IF NOT EXISTS print_pdf_resources (
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    resource_kind TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    resource_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, render_id, resource_id, resource_kind)
);

CREATE TABLE IF NOT EXISTS print_pdf_findings (
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

CREATE TABLE IF NOT EXISTS print_pdf_decisions (
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

CREATE TABLE IF NOT EXISTS print_pdf_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS print_pdf_history (
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

CREATE TABLE IF NOT EXISTS print_pdf_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    render_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_print_pdf_page_number
ON print_pdf_pages(workspace_id, render_id, page_number);
