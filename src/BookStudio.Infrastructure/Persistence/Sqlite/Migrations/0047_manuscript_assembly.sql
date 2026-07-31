CREATE TABLE IF NOT EXISTS manuscript_assemblies (
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    locale TEXT NOT NULL,
    target_channels_json TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    sections_json TEXT NOT NULL,
    findings_json TEXT NOT NULL,
    manifest_json TEXT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, assembly_id)
);

CREATE TABLE IF NOT EXISTS manuscript_source_bindings (
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    source_id TEXT NOT NULL,
    slice_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    content_digest TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    source_status TEXT NOT NULL,
    project_id TEXT NOT NULL,
    included INTEGER NOT NULL,
    PRIMARY KEY (workspace_id, assembly_id, source_id, revision, included)
);

CREATE TABLE IF NOT EXISTS manuscript_sections (
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    section_id TEXT NOT NULL,
    section_kind TEXT NOT NULL,
    section_order INTEGER NOT NULL,
    section_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, assembly_id, section_id)
);

CREATE TABLE IF NOT EXISTS manuscript_nodes (
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    section_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    node_kind TEXT NOT NULL,
    node_order INTEGER NOT NULL,
    content_digest TEXT NOT NULL,
    node_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, assembly_id, node_id)
);

CREATE TABLE IF NOT EXISTS manuscript_findings (
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    code TEXT NOT NULL,
    severity TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, assembly_id, finding_id)
);

CREATE TABLE IF NOT EXISTS manuscript_decisions (
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
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

CREATE TABLE IF NOT EXISTS manuscript_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS manuscript_history (
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, assembly_id, revision)
);

CREATE TABLE IF NOT EXISTS manuscript_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    assembly_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_manuscript_section_order
ON manuscript_sections(workspace_id, assembly_id, section_order);

CREATE UNIQUE INDEX IF NOT EXISTS ux_manuscript_node_order
ON manuscript_nodes(workspace_id, assembly_id, section_id, node_order);
