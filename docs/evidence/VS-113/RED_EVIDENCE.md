# VS-113 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed DOCX render request, exact upstream authority, package part, relationship, style, resource, finding, decision or state contracts;
- no deterministic OPC package, relationship graph, style/numbering or metadata construction;
- no compatibility, accessibility, editability or relationship-safety evidence;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for DOCX rendering.

The implementation journey must turn these missing capabilities GREEN without weakening VS-112 or earlier guarantees.

## RED-E — external/governance-facing

The cumulative journey currently cannot prove that:

1. one exact approved VS-112 authority renders to deterministic editable DOCX output;
2. package parts, relationships, content types, styles, numbering and document order are explicit and reproducible;
3. external relationships, macros, unsafe embeddings and path escapes fail closed;
4. resources retain rights, integrity and accessibility evidence;
5. stale, missing, digest-mismatched, cross-workspace or non-approved inputs fail closed;
6. blocking compatibility, accessibility or editability findings prevent approval;
7. restart and exact replay preserve one authoritative output and one deterministic Outbox effect;
8. transaction failure leaves no partial approved render.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.
