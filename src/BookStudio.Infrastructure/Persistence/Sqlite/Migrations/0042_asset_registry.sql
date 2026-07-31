CREATE TABLE IF NOT EXISTS visual_assets (
    workspace_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    visual_brief_id TEXT NOT NULL,
    expected_visual_brief_revision INTEGER NOT NULL,
    expected_visual_brief_digest TEXT NOT NULL,
    asset_type TEXT NOT NULL,
    source_adapter TEXT NOT NULL,
    storage_root TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    canonical_storage_identity TEXT NOT NULL,
    media_format TEXT NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    color_profile TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    causal_snapshot_json TEXT NOT NULL,
    generation_parameters_json TEXT NOT NULL,
    status TEXT NOT NULL,
    decision_reason TEXT NULL,
    superseded_by_asset_id TEXT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, asset_id),
    UNIQUE (workspace_id, canonical_storage_identity),
    UNIQUE (workspace_id, content_digest, asset_id)
);

CREATE TABLE IF NOT EXISTS asset_provenance_evidence (
    workspace_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    source_uri TEXT NOT NULL,
    prompt_digest TEXT NOT NULL,
    input_lineage_json TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    captured_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, asset_id),
    FOREIGN KEY (workspace_id, asset_id) REFERENCES visual_assets(workspace_id, asset_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS asset_rights_evidence (
    workspace_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    license_kind TEXT NOT NULL,
    license_reference TEXT NOT NULL,
    rights_holder TEXT NOT NULL,
    territory TEXT NOT NULL,
    valid_from_utc TEXT NULL,
    valid_until_utc TEXT NULL,
    evidence_digest TEXT NOT NULL,
    PRIMARY KEY (workspace_id, asset_id),
    FOREIGN KEY (workspace_id, asset_id) REFERENCES visual_assets(workspace_id, asset_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS asset_accessibility_evidence (
    workspace_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    alt_text TEXT NOT NULL,
    long_description TEXT NOT NULL,
    language TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    PRIMARY KEY (workspace_id, asset_id),
    FOREIGN KEY (workspace_id, asset_id) REFERENCES visual_assets(workspace_id, asset_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS asset_technical_validations (
    workspace_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    validation_id TEXT NOT NULL,
    validation_kind TEXT NOT NULL,
    outcome TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, asset_id, validation_id),
    FOREIGN KEY (workspace_id, asset_id) REFERENCES visual_assets(workspace_id, asset_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS asset_relationships (
    workspace_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    relationship_id TEXT NOT NULL,
    relationship_kind TEXT NOT NULL,
    related_asset_id TEXT NOT NULL,
    evidence TEXT NOT NULL,
    PRIMARY KEY (workspace_id, asset_id, relationship_id),
    FOREIGN KEY (workspace_id, asset_id) REFERENCES visual_assets(workspace_id, asset_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS asset_registry_history (
    workspace_id TEXT NOT NULL,
    history_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, history_id),
    UNIQUE (workspace_id, asset_id, revision, event_type)
);

CREATE TABLE IF NOT EXISTS asset_registry_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, request_id),
    UNIQUE (workspace_id, request_fingerprint)
);

CREATE INDEX IF NOT EXISTS ix_visual_assets_brief_authority
    ON visual_assets(workspace_id, visual_brief_id, expected_visual_brief_revision, expected_visual_brief_digest);
CREATE INDEX IF NOT EXISTS ix_visual_assets_status
    ON visual_assets(workspace_id, project_id, status);
CREATE INDEX IF NOT EXISTS ix_asset_registry_history_asset
    ON asset_registry_history(workspace_id, asset_id, revision);
CREATE INDEX IF NOT EXISTS ix_asset_relationships_related
    ON asset_relationships(workspace_id, related_asset_id);
