# Autopilot Layer Instructions

## Allowed

- Workflow definitions, next-step resolution and stop conditions.
- Planning jobs and human gates through Application contracts.
- Deterministic workflow state decisions based on versioned inputs.
- References to Application and Domain.

## Forbidden

- Direct database, queue or OpenCode transport implementation.
- Canonical state mutation outside Application use cases.
- Editorial content generation.
- Provider-specific assumptions.
- Silent gate bypass or unbounded repair loops.

Every workflow change must define versioning, idempotency, recovery and invalidation.
