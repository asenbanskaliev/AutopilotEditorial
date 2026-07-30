# VS-094 - AI provenance disclosure

## Intent

Classify, preserve, and disclose the human, AI-assisted, or AI-generated provenance of content and assets authorized by VS-093 in a durable, reproducible, and auditable way.

## Behaviors

1. Each record declares workspace, project, asset, version, exact rights authority, actor, and causal snapshot.
2. Only an approved, current, non-stale VS-093 rights record can authorize provenance classification.
3. Classification supports HUMAN_CREATED, AI_ASSISTED, AI_GENERATED, MIXED, and UNKNOWN.
4. Provider, model, version, date, reproducible prompt reference, human transformations, declared scope, and evidence are recorded.
5. Publication disclosures are generated per channel and version with text, locale, format, and policy version.
6. Incomplete or contradictory evidence, insufficient rights, or UNKNOWN classification blocks approval and fails closed.
7. Drift in asset, rights, model, policy, or evidence marks the record STALE.
8. History is append-only and reconstructs classification, evaluation, decision, reopen, revoke, and stale transitions.
9. Exact replay is idempotent; conflicting identity or request reuse fails by comparing the real payload.
10. Optimistic concurrency, atomic rollback, workspace isolation, restart recovery, and exactly-once Outbox delivery are mandatory.

## Invariants

- No record exists without exact, current VS-093 authority.
- A record cannot mix workspaces, projects, assets, versions, or authorities.
- Approved state cannot exist with incomplete evidence, UNKNOWN classification, or open blockers.
- Failed transitions leave no partial record, disclosure, history, or event.
- Replay does not duplicate records, history, disclosures, or events.

## Gates

- Exact authority from approved rights and licenses.
- Reproducible classification and evidence.
- Disclosures per channel and version.
- Fail-closed blocking for uncertainty, drift, or insufficient rights.
- Replay, concurrency, rollback, restart, and workspace isolation.
- Exactly-once Outbox.
- DUAL_GREEN, Auditoria M, Meta-Audit, RetroSpec, and complete CI.
