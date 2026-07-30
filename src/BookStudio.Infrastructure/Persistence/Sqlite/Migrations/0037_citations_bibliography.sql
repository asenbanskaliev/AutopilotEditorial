CREATE TABLE citation_bibliographies (
    workspace_id TEXT NOT NULL,
    bibliography_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    claim_verification_id TEXT NOT NULL,
    expected_claim_verification_revision INTEGER NOT NULL,
    expected_claim_verification_digest TEXT NOT NULL,
    version INTEGER NOT NULL,
    citation_style TEXT NOT NULL,
    locale TEXT NOT NULL,
    actor TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    revision INTEGER NOT NULL,
    status TEXT NOT NULL,
    decision TEXT NULL,
    decision_reason TEXT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, bibliography_id)
);

CREATE TABLE citations (
    workspace_id TEXT NOT NULL,
    bibliography_id TEXT NOT NULL,
    citation_id TEXT NOT NULL,
    claim_id TEXT NOT NULL,
    source_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    location TEXT NOT NULL,
    locator TEXT NOT NULL,
    rendered_text TEXT NOT NULL,
    metadata_valid INTEGER NOT NULL,
    link_valid INTEGER NOT NULL,
    is_current INTEGER NOT NULL,
    evidence TEXT NOT NULL,
    PRIMARY KEY(workspace_id, bibliography_id, citation_id),
    FOREIGN KEY(workspace_id, bibliography_id) REFERENCES citation_bibliographies(workspace_id, bibliography_id) ON DELETE CASCADE
);

CREATE TABLE bibliography_entries (
    workspace_id TEXT NOT NULL,
    bibliography_id TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    source_id TEXT NOT NULL,
    canonical_key TEXT NOT NULL,
    title TEXT NOT NULL,
    author TEXT NULL,
    publisher TEXT NULL,
    year INTEGER NULL,
    doi TEXT NULL,
    isbn TEXT NULL,
    url TEXT NULL,
    rendered_text TEXT NOT NULL,
    metadata_valid INTEGER NOT NULL,
    is_current INTEGER NOT NULL,
    evidence TEXT NOT NULL,
    PRIMARY KEY(workspace_id, bibliography_id, entry_id),
    UNIQUE(workspace_id, bibliography_id, canonical_key),
    FOREIGN KEY(workspace_id, bibliography_id) REFERENCES citation_bibliographies(workspace_id, bibliography_id) ON DELETE CASCADE
);

CREATE TABLE citation_bibliography_history (
    workspace_id TEXT NOT NULL,
    bibliography_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NULL,
    payload_json TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, bibliography_id, revision)
);

CREATE TABLE citation_bibliography_receipts (
    workspace_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    bibliography_id TEXT NOT NULL,
    action TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    resulting_revision INTEGER NOT NULL,
    message_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY(workspace_id, request_id)
);
