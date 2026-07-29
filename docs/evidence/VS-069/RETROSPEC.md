# VS-069 RetroSpec

## Confirmed specification

A memory commit is authorized only by an active exact chapter lock. A delta is immutable after proposal, validated against current projection digests and committed atomically across projection entries, previous-state history, lifecycle state and Outbox.

## Clarifications learned during implementation

- Supported projection families are explicitly bounded to KNOWLEDGE, STATE, TIMELINE and PLOT_THREAD.
- Entries use UPSERT or RETRACT; duplicate projection/entity pairs are rejected.
- Empty expected digest means creation is allowed; a supplied digest is an optimistic precondition.
- Reopened or replaced locks invalidate an uncommitted delta and produce STALE without projection mutation.
- Commit replay returns the stored terminal result and does not repeat history or events.

No spec change requires remediation. RetroSpec: synced.
