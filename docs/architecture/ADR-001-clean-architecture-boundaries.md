# ADR-001 — Clean Architecture boundaries

## Status

Accepted.

## Context

BookStudio combines editorial domain rules, durable workflows, external model providers, MCP protocol adapters, storage, rendering and user interfaces. Without explicit dependency rules, business decisions could leak into transport or infrastructure and become impossible to test or reuse.

## Decision

Use the dependency direction:

```text
Hosts / adapters
→ Infrastructure / orchestration
→ Application
→ Domain
```

### Domain

Contains entities, value objects, invariants and domain services. It has no project dependencies and no I/O or provider concerns.

### Application

Contains use cases, ports, commands, queries and contracts. It depends only on Domain and does not choose concrete storage, HTTP, model or workflow implementations.

### Infrastructure

Implements Application ports for persistence, filesystem, queues, rendering and external adapters. It may depend on Application and Domain but does not own editorial policy.

### MCP

Translates MCP protocol requests and responses to Application use cases. It does not contain domain rules, memory truth or durable workflow state.

### OpenCode

Implements OpenCode session, prompt, event and compatibility transport behind Application contracts.

### Autopilot

Plans workflows and next steps, but all canonical state transitions pass through Application use cases.

### Worker

Hosts durable job execution. It may compose Autopilot, Infrastructure and OpenCode but does not mutate domain state directly.

### Control Center

Hosts presentation and composition. It does not access persistence directly.

## Enforcement

- `architecture-policy.json` is canonical.
- Python governance tests validate solution and project XML.
- The .NET architecture executable validates project XML and compiled PE assembly references.
- Scoped `AGENTS.md` files guide AI and human contributors.
- Any policy change requires an ADR update and affected-test review.

## Consequences

- Some cross-layer conveniences are rejected.
- Ports must be introduced before concrete adapters.
- Hosts may compose dependencies but cannot absorb business behavior.
- The policy becomes a versioned public engineering contract.
