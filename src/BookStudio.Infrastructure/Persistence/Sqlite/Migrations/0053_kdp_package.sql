CREATE TABLE IF NOT EXISTS kdp_packages (
    workspace_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    authority_json TEXT NOT NULL,
    metadata_json TEXT NOT NULL,
    artifacts_json TEXT NOT NULL,
    marketplace TEXT NOT NULL,
    language TEXT NOT NULL,
    format_profile TEXT NOT NULL,
    profile_version TEXT NOT NULL,
    manifest_json TEXT NULL,
    findings_json TEXT NOT NULL,
    evidence_digest TEXT NULL,
    status TEXT NOT NULL,
    revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, package_id)
);

CREATE TABLE IF NOT EXISTS kdp_package_metadata_revisions (
    workspace_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    metadata_json TEXT NOT NULL,
    profile_version TEXT NOT NULL,
    metadata_digest TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, package_id, revision)
);

CREATE TABLE IF NOT EXISTS kdp_package_findings (
    workspace_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    code TEXT NOT NULL,
    severity TEXT NOT NULL,
    field TEXT NOT NULL,
    rule_id TEXT NOT NULL,
    evidence_digest TEXT NOT NULL,
    status TEXT NOT NULL,
    finding_json TEXT NOT NULL,
    PRIMARY KEY (workspace_id, package_id, finding_id)
);

CREATE TABLE IF NOT EXISTS kdp_package_manifests (
    workspace_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    manifest_digest TEXT NOT NULL,
    package_digest TEXT NOT NULL,
    canonical_json TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, package_id, revision)
);

CREATE TABLE IF NOT EXISTS kdp_package_decisions (
    workspace_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
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

CREATE TABLE IF NOT EXISTS kdp_package_receipts (
    workspace_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_digest TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, operation_id)
);

CREATE TABLE IF NOT EXISTS kdp_package_history (
    workspace_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    operation TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, package_id, revision)
);

CREATE TABLE IF NOT EXISTS kdp_package_outbox (
    message_id TEXT NOT NULL PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    package_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);
