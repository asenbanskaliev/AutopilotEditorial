# VS-063 RetroSpec

## What changed

The repository now supports durable transition audits between exact closed editorial endpoints.

## Learned constraints

- Transition authority must bind both endpoints by workspace, project, identity, version and digest.
- Every transition dimension requires one explicit assessment, including `NOT_APPLICABLE`.
- Blocking findings remain open until a governed terminal decision exists.
- Closure, idempotency receipt and Outbox event must commit atomically.

## Follow-through

Knowledge, character/object and timeline slices must consume transition evidence rather than infer continuity from disconnected text scans.
