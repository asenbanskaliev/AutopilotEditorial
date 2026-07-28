# VS-051 RetroSpec — Discovery journey

## Delivered

A durable discovery journey for editorial projects with structured questions, typed validation, versioned answers, decisions, open-item tracking, fail-closed completion, immutable evidence and transactional completion events.

## Durable rules

- Discovery is isolated by workspace and session identity.
- Question definitions are immutable for a session.
- Answers append versions; prior evidence is never overwritten.
- Required questions and required open items gate completion.
- Completion is terminal for that session version.
- Request replay is governed by immutable fingerprints.
- Completion state and delivery intent are one transaction.
- Reopening requires a future explicit versioned workflow.

Status: VERIFIED.
