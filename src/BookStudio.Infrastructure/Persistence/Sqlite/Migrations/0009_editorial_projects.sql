CREATE TABLE editorial_projects (
    workspace_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    create_request_id TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    project_kind TEXT NOT NULL CHECK (project_kind IN ('FICTION','NON_FICTION','TECHNICAL','EDUCATIONAL','OTHER')),
    language_tag TEXT NOT NULL,
    audience TEXT NOT NULL,
    objective TEXT NOT NULL,
    request_fingerprint TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ACTIVE','ARCHIVED')),
    created_message_id TEXT NOT NULL UNIQUE,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (workspace_id, project_id)
);

CREATE INDEX ix_editorial_projects_workspace
    ON editorial_projects(workspace_id, status, created_at_utc, project_id);
