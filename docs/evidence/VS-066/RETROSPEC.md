# VS-066 RetroSpec

## What changed

The repository now maintains a durable narrative timeline and versioned plot threads derived from authoritative knowledge and materialized character/object state.

## Learned constraints

- Timeline authority must be an exact active `FACT` valid at the narrative instant.
- Narrative order and causal order are related but independently validated.
- Causal dependencies must exist, precede their dependents and remain acyclic.
- Plot milestones and resolution require their declared timeline events to be active.
- Replay must compare canonical payload content, not only a client-supplied fingerprint.
- Event activation, plot advancement and resolution commit state, receipt and Outbox atomically.
- Workspace boundaries apply to events, dependencies, threads and receipts.

## Audit feedback incorporated

A compile failure and a journey expectation mismatch were corrected without weakening production invariants. All cumulative journeys subsequently passed.

## Follow-through

`VS-067 Repair patches` must use the durable timeline and plot-thread state to target localized corrections, preserve causal ordering and prove that repairs do not introduce new contradictions.