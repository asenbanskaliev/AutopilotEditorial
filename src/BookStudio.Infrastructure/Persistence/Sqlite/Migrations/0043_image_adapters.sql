CREATE TABLE IF NOT EXISTS image_adapter_requests (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    visual_brief_id TEXT NOT NULL,
    expected_visual_brief_revision INTEGER NOT NULL,
    expected_visual_brief_digest TEXT NOT NULL,
    asset_type TEXT NOT NULL,
    adapter_id TEXT NOT NULL,
    adapter_version TEXT NOT NULL,
    adapter_kind TEXT NOT NULL,
    operation_mode TEXT NOT NULL,
    required_capabilities_json TEXT NOT NULL,
    prompt_digest TEXT NOT NULL,
    generation_parameters_json TEXT NOT NULL,
    output_policy_json TEXT NOT NULL,
    retry_policy_json TEXT NOT NULL,
    status TEXT NOT NULL,
    last_error_json TEXT NULL,
    request_fingerprint TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, request_id),
    UNIQUE (workspace_id, request_fingerprint)
);

CREATE TABLE IF NOT EXISTS image_adapter_attempts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    attempt_number INTEGER NOT NULL,
    adapter_id TEXT NOT NULL,
    adapter_version TEXT NOT NULL,
    result_json TEXT NOT NULL,
    provider_evidence_json TEXT NOT NULL,
    provider_evidence_digest TEXT NOT NULL,
    actor TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    started_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, attempt_id),
    UNIQUE (workspace_id, request_id, attempt_number),
    FOREIGN KEY (workspace_id, request_id)
        REFERENCES image_adapter_requests(workspace_id, request_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS image_adapter_outputs (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    output_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    storage_root TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    canonical_storage_identity TEXT NOT NULL,
    media_format TEXT NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    bytes INTEGER NOT NULL,
    color_profile TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    technical_metadata_json TEXT NOT NULL,
    provenance_json TEXT NOT NULL,
    relationships_json TEXT NOT NULL,
    asset_id TEXT NULL,
    asset_revision INTEGER NULL,
    asset_outbox_message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, output_id),
    UNIQUE (workspace_id, request_id, content_digest),
    UNIQUE (workspace_id, canonical_storage_identity),
    FOREIGN KEY (workspace_id, request_id)
        REFERENCES image_adapter_requests(workspace_id, request_id) ON DELETE CASCADE,
    FOREIGN KEY (workspace_id, attempt_id)
        REFERENCES image_adapter_attempts(workspace_id, attempt_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS image_adapter_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id),
    UNIQUE (workspace_id, request_id, request_fingerprint, payload_digest)
);

CREATE TABLE IF NOT EXISTS image_adapter_history (
    workspace_id TEXT NOT NULL,
    history_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, history_id),
    UNIQUE (workspace_id, request_id, revision, event_type),
    FOREIGN KEY (workspace_id, request_id)
        REFERENCES image_adapter_requests(workspace_id, request_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_image_adapter_requests_authority
    ON image_adapter_requests(
        workspace_id,
        visual_brief_id,
        expected_visual_brief_revision,
        expected_visual_brief_digest
    );
CREATE INDEX IF NOT EXISTS ix_image_adapter_requests_status
    ON image_adapter_requests(workspace_id, project_id, status);
CREATE INDEX IF NOT EXISTS ix_image_adapter_attempts_request
    ON image_adapter_attempts(workspace_id, request_id, attempt_number);
CREATE INDEX IF NOT EXISTS ix_image_adapter_outputs_request
    ON image_adapter_outputs(workspace_id, request_id);
CREATE INDEX IF NOT EXISTS ix_image_adapter_outputs_asset
    ON image_adapter_outputs(workspace_id, asset_id);
CREATE INDEX IF NOT EXISTS ix_image_adapter_history_request
    ON image_adapter_history(workspace_id, request_id, revision);
