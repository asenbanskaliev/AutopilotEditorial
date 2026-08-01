CREATE TABLE IF NOT EXISTS professional_releases (
    workspace_id TEXT NOT NULL,
    release_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    channel TEXT NOT NULL,
    semantic_version TEXT NOT NULL,
    locale TEXT NOT NULL,
    supersedes_release_id TEXT NULL,
    artifacts_json TEXT NOT NULL,
    manifest_json TEXT NULL,
    inventory_digest TEXT NULL,
    evidence_digest TEXT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, release_id)
);

CREATE TABLE IF NOT EXISTS professional_release_artifacts (
    workspace_id TEXT NOT NULL,
    release_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    logical_name TEXT NOT NULL,
    media_type TEXT NOT NULL,
    byte_length INTEGER NOT NULL,
    digest TEXT NOT NULL,
    provenance TEXT NOT NULL,
    source_authority TEXT NOT NULL,
    required INTEGER NOT NULL,
    artifact_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, release_id, revision, logical_name)
);

CREATE TABLE IF NOT EXISTS professional_release_manifests (
    workspace_id TEXT NOT NULL,
    release_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    manifest_digest TEXT NOT NULL,
    inventory_digest TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    frozen_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, release_id, revision)
);

CREATE TABLE IF NOT EXISTS professional_release_decisions (
    workspace_id TEXT NOT NULL,
    release_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    decision TEXT NOT NULL,
    reason TEXT NOT NULL,
    evidence TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    actor TEXT NOT NULL,
    revision INTEGER NOT NULL,
    decided_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS professional_release_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    release_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS professional_release_history (
    workspace_id TEXT NOT NULL,
    release_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, release_id, revision)
);

CREATE TABLE IF NOT EXISTS professional_release_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    release_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    published_at_utc TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_professional_release_active_version
    ON professional_releases(workspace_id, project_id, channel, semantic_version)
    WHERE status <> 'Superseded';

CREATE INDEX IF NOT EXISTS ix_professional_release_history_lookup
    ON professional_release_history(workspace_id, release_id, revision DESC);

CREATE INDEX IF NOT EXISTS ix_professional_release_outbox_pending
    ON professional_release_outbox(published_at_utc, created_at_utc);
