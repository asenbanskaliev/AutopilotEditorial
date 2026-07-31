# VS-101 RetroSpec

## What the implementation clarified

- Asset identity must combine immutable digest with canonical storage identity; neither can be changed in place.
- Exact VS-100 authority is required both at registration and at approval because authority can drift between transitions.
- Provenance, rights, accessibility and technical validation are first-class durable records, not descriptive metadata.
- Idempotency receipts must survive restart and bind request identity, fingerprint and payload digest.
- State changes, evidence, relationships, history and Outbox publication intent form one atomic consistency boundary.

## Specification refinements retained

- Approval remains fail-closed for missing, failed or stale evidence.
- Repair creates a new validated revision without erasing append-only history.
- Supersession is explicit and preserves predecessor/successor traceability.
- Cross-workspace, cross-project and authority mismatches are rejected before writes.
- Governance tests verify durable authority, lifecycle surface, schema completeness, concurrency and transaction markers.

## Follow-forward

Downstream slices must consume only approved, non-stale assets and preserve the asset id, revision, digest, visual-brief authority and rights context in their own causal snapshots.
