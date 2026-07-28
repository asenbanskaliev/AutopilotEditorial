# VS-064 RetroSpec

## What changed

The repository now maintains durable narrative knowledge state tied to an exact closed transition, including attributable activation and disclosure events.

## Learned constraints

- Knowledge authority must reference the exact transition close message.
- Facts, beliefs and secrets have different contradiction and disclosure semantics.
- Beliefs may diverge from facts; contradiction gates apply only to active overlapping facts.
- Contradiction must be checked again during activation because multiple drafts may coexist.
- Knowers and excluded actors remain disjoint.
- Create replay must compare every immutable field, not only identity and statement.
- Activation and disclosure receipts, state changes and Outbox events commit atomically.
- Attribution is part of durable state and survives restart.

## Audit feedback incorporated

AUDIT_REMEDIATION_001 added regression scenarios for divergent beliefs, competing contradictory fact drafts, strict replay and disclosure exactly-once. No observable requirement was weakened.

## Follow-through

Character, object and timeline slices must consume this durable knowledge state instead of reconstructing it from text. They must preserve fact/belief/secret semantics and use disclosure history as the authoritative audience evolution.
