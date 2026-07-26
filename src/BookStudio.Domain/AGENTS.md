# Domain Layer Instructions

## Allowed

- Entities, value objects, domain events and invariants.
- Pure domain services.
- Deterministic calculations without I/O.
- Errors and result types that express domain rules.

## Forbidden

- Database, filesystem, network, MCP or OpenCode dependencies.
- ASP.NET, Entity Framework or provider SDKs.
- Reading clocks, environment variables or global process state directly.
- Workflow scheduling, logging or UI concerns.
- References to any other BookStudio project.

Every change must include tests for the invariant it introduces.
