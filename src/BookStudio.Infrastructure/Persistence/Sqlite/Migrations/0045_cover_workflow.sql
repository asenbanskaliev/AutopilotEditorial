CREATE TABLE IF NOT EXISTS cover_projects (
    workspace_id TEXT NOT NULL,
    cover_project_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    required_channels_json TEXT NOT NULL,
    title TEXT NOT NULL,
    subtitle TEXT NULL,
    author TEXT NOT NULL,
    series TEXT NULL,
    imprint TEXT NULL,
    blurb TEXT NULL,
    isbn TEXT NULL,
    selected_variant_id TEXT NULL,
    status TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, cover_project_id),
    UNIQUE (workspace_id, request_fingerprint)
);

CREATE TABLE IF NOT EXISTS cover_variants (
    workspace_id TEXT NOT NULL,
    cover_project_id TEXT NOT NULL,
    variant_id TEXT NOT NULL,
    channel TEXT NOT NULL,
    variant_kind TEXT NOT NULL,
    source_variant_id TEXT NULL,
    geometry_json TEXT NOT NULL,
    typography_json TEXT NOT NULL,
    export_profile TEXT NOT NULL,
    artifact_digest TEXT NOT NULL,
    status TEXT NOT NULL,
    decision_reason TEXT NULL,
    revision INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, variant_id),
    UNIQUE (workspace_id, cover_project_id, channel, artifact_digest),
    FOREIGN KEY (workspace_id, cover_project_id)
        REFERENCES cover_projects(workspace_id, cover_project_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS cover_placements (
    workspace_id TEXT NOT NULL,
    cover_project_id TEXT NOT NULL,
    variant_id TEXT NOT NULL,
    placement_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    asset_revision INTEGER NOT NULL,
    asset_digest TEXT NOT NULL,
    role TEXT NOT NULL,
    bounds_json TEXT NOT NULL,
    crop_mode TEXT NOT NULL,
    lineage_evidence_digest TEXT NOT NULL,
    PRIMARY KEY (workspace_id, placement_id),
    FOREIGN KEY (workspace_id, variant_id)
        REFERENCES cover_variants(workspace_id, variant_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS cover_validations (
    workspace_id TEXT NOT NULL,
    cover_project_id TEXT NOT NULL,
    variant_id TEXT NOT NULL,
    validation_id TEXT NOT NULL,
    validation_kind TEXT NOT NULL,
    outcome TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    PRIMARY KEY (workspace_id, validation_id),
    FOREIGN KEY (workspace_id, variant_id)
        REFERENCES cover_variants(workspace_id, variant_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS cover_decisions (
    workspace_id TEXT NOT NULL,
    cover_project_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    variant_id TEXT NOT NULL,
    decision TEXT NOT NULL,
    reason TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    actor TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, request_id)
);

CREATE TABLE IF NOT EXISTS cover_workflow_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    cover_project_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS cover_workflow_history (
    workspace_id TEXT NOT NULL,
    history_id TEXT NOT NULL,
    cover_project_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, history_id),
    UNIQUE (workspace_id, cover_project_id, revision, event_type)
);

CREATE INDEX IF NOT EXISTS ix_cover_projects_status
    ON cover_projects(workspace_id, project_id, status);
CREATE INDEX IF NOT EXISTS ix_cover_variants_project
    ON cover_variants(workspace_id, cover_project_id, channel, status);
CREATE INDEX IF NOT EXISTS ix_cover_placements_variant
    ON cover_placements(workspace_id, variant_id);
CREATE INDEX IF NOT EXISTS ix_cover_validations_variant
    ON cover_validations(workspace_id, variant_id);
CREATE INDEX IF NOT EXISTS ix_cover_history_project
    ON cover_workflow_history(workspace_id, cover_project_id, revision);
