# Application Layer Instructions

## Allowed

- Commands, queries, use cases and orchestration within one application transaction.
- Ports for persistence, files, clocks, queues, models and renderers.
- DTOs, validators and authorization requirements.
- References to BookStudio.Domain only.

## Forbidden

- Concrete database, HTTP, filesystem or provider implementations.
- ASP.NET controllers, MCP handlers or UI components.
- Entity Framework and infrastructure packages.
- Direct process or environment access.
- Editorial rules that belong in Domain.

Every public use case must define inputs, outputs, errors, idempotency and authorization.
