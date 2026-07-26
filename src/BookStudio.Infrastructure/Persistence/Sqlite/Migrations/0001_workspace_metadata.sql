CREATE TABLE workspace_metadata (
    key TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
) STRICT;
