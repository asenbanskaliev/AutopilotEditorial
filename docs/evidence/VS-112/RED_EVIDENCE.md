# VS-112 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed print render request, exact upstream authority, page geometry, font/image resource, finding, decision or state contracts;
- no deterministic pagination, page-box, metadata or artifact-digest construction;
- no PDF preflight-compatible validation evidence;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for print rendering.

The implementation journey must turn these missing capabilities GREEN without weakening VS-111 or earlier guarantees.

## RED-E — external/governance-facing

The cumulative journey currently cannot prove that:

1. one exact approved VS-111 authority renders to deterministic print output;
2. trim, bleed, margins, binding, pagination and page transitions are explicit and reproducible;
3. fonts are embedded or governed and missing glyphs fail closed;
4. image rights, color profile and effective DPI are validated;
5. stale, missing, digest-mismatched, cross-workspace or non-approved inputs fail closed;
6. blocking PDF preflight-compatible findings prevent approval;
7. restart and exact replay preserve one authoritative output and one deterministic Outbox effect;
8. transaction failure leaves no partial approved render.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.
