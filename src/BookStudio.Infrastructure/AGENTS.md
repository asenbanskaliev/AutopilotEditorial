# Infrastructure Layer Instructions

## Allowed

- Implement Application ports.
- SQLite, PostgreSQL, filesystem, Outbox, renderers and external adapters.
- Serialization, migrations, retries and resource management.
- References to Application and Domain.

## Forbidden

- New editorial rules or policy decisions.
- Direct UI or MCP protocol behavior.
- Bypassing Application use cases for canonical state changes.
- Returning provider-specific types through Application contracts.
- Hidden global state or secrets committed to the repository.

Adapters must be replaceable and covered by contract or integration tests.
