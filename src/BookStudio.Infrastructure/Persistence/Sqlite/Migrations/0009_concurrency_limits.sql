CREATE TABLE IF NOT EXISTS concurrency_limits (
  scope_type TEXT NOT NULL,
  scope_key TEXT NOT NULL,
  capacity INTEGER NOT NULL CHECK(capacity > 0),
  version INTEGER NOT NULL CHECK(version > 0),
  updated_by TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  PRIMARY KEY(scope_type, scope_key)
);

CREATE TABLE IF NOT EXISTS concurrency_grants (
  grant_id TEXT PRIMARY KEY,
  acquire_request_id TEXT NOT NULL UNIQUE,
  owner_id TEXT NOT NULL,
  priority INTEGER NOT NULL,
  generation INTEGER NOT NULL CHECK(generation > 0),
  status TEXT NOT NULL CHECK(status IN ('ACTIVE','RELEASED','EXPIRED')),
  scopes_json TEXT NOT NULL,
  acquired_at_utc TEXT NOT NULL,
  lease_until_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS concurrency_requests (
  request_id TEXT PRIMARY KEY,
  operation TEXT NOT NULL CHECK(operation IN ('ACQUIRE','RENEW','RELEASE')),
  grant_id TEXT NULL,
  owner_id TEXT NOT NULL,
  generation INTEGER NULL,
  request_fingerprint TEXT NOT NULL,
  result_json TEXT NOT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_concurrency_grants_status_lease
  ON concurrency_grants(status, lease_until_utc);
