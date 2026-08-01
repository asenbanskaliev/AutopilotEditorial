# VS-114 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed accessibility request, exact upstream authority, analyzer execution, normalized finding, manual-review, waiver, decision or state contracts;
- no deterministic analyzer orchestration, rule-profile binding or evidence digest construction;
- no fail-closed combination of automated findings and manual-review requirements;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for accessibility runs.

The implementation journey must turn these missing capabilities GREEN without weakening VS-113 or earlier guarantees.

## RED-E — external/governance-facing

The cumulative journey currently cannot prove that:

1. one exact approved VS-113 authority produces deterministic accessibility evidence;
2. analyzer identity, version, rules, input and output digests are explicit and reproducible;
3. document structure, reading order, language, navigation, links, tables, images, alternatives and contrast are normalized into stable findings;
4. required manual reviews are durable, evidenced and cannot silently erase automated findings;
5. stale, missing, digest-mismatched, cross-workspace or non-approved inputs fail closed;
6. blocking findings or incomplete manual review prevent approval unless an explicit governed waiver applies;
7. restart and exact replay preserve one authoritative result and one deterministic Outbox effect;
8. transaction failure leaves no partial approved accessibility state.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.