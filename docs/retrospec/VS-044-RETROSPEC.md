# VS-044 RetroSpec — Human gate inbox

## Delivered

A durable human-decision inbox for Autopilot workflows with idempotent creation, lease-based claims, immutable terminal decisions, expiry, cancellation, exactly-once resume intent and restart recovery.

## Durable rules

- Human gates pause workflows durably before any decision is requested.
- Only the live claim owner may decide.
- Terminal decisions are immutable.
- Resume identity is deterministic and persisted with the decision.
- Delivery remains Outbox-based and at-least-once; consumers are idempotent.
- Expired or cancelled gates never resume workflows.

## Correction discovered by CI

The restart assertion was strengthened to establish non-null durability before reading the persisted resume message ID, preserving nullable-reference fail-closed compilation.

Status: VERIFIED.
