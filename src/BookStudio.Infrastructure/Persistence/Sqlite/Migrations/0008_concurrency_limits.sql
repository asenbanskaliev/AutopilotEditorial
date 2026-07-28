CREATE TABLE concurrency_limits (
    scope_type TEXT NOT NULL,
    scope_key TEXT NOT NULL,
    capacity INTEGER NOT NULL CHECK (capacity > 0),
    version INTEGER NOT NULL CHECK (version > 0),
    updated_by TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (scope_type, scope_key)
);

CREATE TABLE concurrency_grants (
    grant_id TEXT PRIMARY KEY NOT NULL,
    acquire_request_id TEXT NOT NULL UNIQUE,
    owner_id TEXT NOT NULL,
    priority INTEGER NOT NULL,
    generation INTEGER NOT NULL CHECK (generation > 0),
    status TEXT NOT NULL CHECK (status IN ('ACTIVE','RELEASED','EXPIRED')),
    acquired_at_utc TEXT NOT NULL,
    lease_until_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE concurrency_grant_scopes (
    grant_id TEXT NOT NULL,
    scope_type TEXT NOT NULL,
    scope_key TEXT NOT NULL,
    units INTEGER NOT NULL CHECK (units > 0),
    PRIMARY KEY (grant_id, scope_type, scope_key),
    FOREIGN KEY (grant_id) REFERENCES concurrency_grants(grant_id) ON DELETE CASCADE
);

CREATE TABLE concurrency_requests (
    request_id TEXT PRIMARY KEY NOT NULL,
    operation TEXT NOT NULL CHECK (operation IN ('ACQUIRE','RENEW','RELEASE')),
    grant_id TEXT NULL,
    owner_id TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    result_status TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE INDEX ix_concurrency_active_scopes
    ON concurrency_grants(status, lease_until_utc, grant_id);
CREATE INDEX ix_concurrency_grant_scopes_lookup
    ON concurrency_grant_scopes(scope_type, scope_key, grant_id);
