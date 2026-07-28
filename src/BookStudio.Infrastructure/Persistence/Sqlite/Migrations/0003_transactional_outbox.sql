CREATE TABLE autopilot_state (
    state_key TEXT PRIMARY KEY NOT NULL,
    state_value TEXT NOT NULL,
    state_version INTEGER NOT NULL CHECK (state_version >= 1),
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE transactional_outbox_operations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    request_fingerprint TEXT NOT NULL,
    state_key TEXT NOT NULL,
    state_version INTEGER NOT NULL CHECK (state_version >= 1),
    message_ids_json TEXT NOT NULL,
    committed_at_utc TEXT NOT NULL
);
