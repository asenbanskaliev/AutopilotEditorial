# VS-105 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed accessibility case, visual-use, textual-alternative, contrast, finding, decision or state contracts;
- no exact authority validation across VS-101 assets, VS-103 visual audits and VS-104 approved covers;
- no deterministic orchestration for alt text, decorative classification, long descriptions, text-in-image alternatives, contrast or reading order;
- no replay-safe, concurrency-safe durable store, append-only history or Outbox for visual accessibility.

The implementation journey must turn these missing capabilities GREEN without weakening existing VS-100 through VS-104 guarantees.

## RED-E — external/governance-facing

The cumulative user journey currently cannot prove that:

1. each meaningful visual has an approved textual alternative;
2. decorative classification is explicit and governed;
3. complex visuals expose an adequate long description;
4. essential text embedded in imagery has an equivalent alternative;
5. contrast and reading order are validated with durable evidence;
6. stale or cross-boundary upstream authority fails closed;
7. restart and exact replay preserve one authoritative result and one deterministic Outbox effect.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.
