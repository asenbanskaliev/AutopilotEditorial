# VS-095 - Legal risk workflow

## Intent

Detect, classify, route, and preserve legal-risk decisions for manuscript content and assets authorized by VS-094 provenance records, with mandatory fail-closed human review where policy requires it.

## Behaviors

1. Each case declares workspace, project, subject asset/content version, exact VS-094 provenance authority, actor, jurisdiction set, policy version, and causal snapshot.
2. Only an approved, current, non-stale VS-094 provenance record can authorize a legal-risk case.
3. Risk categories include PERSON_PRIVACY, DEFAMATION, PUBLICITY_RIGHTS, TRADEMARK, COPYRIGHT, SENSITIVE_CLAIM, REGULATED_CONTENT, CONTRACTUAL_RESTRICTION, and OTHER.
4. Findings record the cited passage or asset, affected person or organization, jurisdiction, severity, confidence, rationale, evidence, and proposed mitigation.
5. Policy evaluation determines whether publication is allowed, blocked, repair-required, or requires mandatory human legal review.
6. High-severity, uncertain, contradictory, or policy-mandated findings fail closed and cannot be approved without an explicit qualified human decision.
7. Human review records reviewer identity, role, scope, decision, rationale, evidence, timestamp, and any conditions or expiry.
8. Drift in content, asset, provenance, policy, jurisdiction, evidence, or review conditions marks the case STALE.
9. History is append-only and reconstructs creation, evaluation, escalation, human review, decision, reopen, revoke, and stale transitions.
10. Exact replay is idempotent; conflicting request reuse, optimistic concurrency violations, cross-workspace access, or authority mismatch fail without partial writes.
11. State transitions, receipts, findings, reviews, and Outbox messages are atomic and restart-safe.
12. Exactly-once Outbox delivery emits deterministic legal-risk lifecycle events.

## Invariants

- No legal-risk case exists without exact, approved, current VS-094 authority.
- A case cannot mix workspaces, projects, assets/content versions, provenance authorities, or jurisdiction policy contexts.
- APPROVED cannot exist with unresolved blocking findings, missing mandatory human review, expired conditions, or stale authority.
- Automated evaluation cannot impersonate or replace a required human legal decision.
- Failed transitions leave no partial case, finding, review, history, receipt, or event.
- Exact replay does not duplicate any durable side effect.

## Gates

- Exact VS-094 authority and drift detection.
- Complete risk taxonomy and evidence-bearing findings.
- Jurisdiction and policy-version evaluation.
- Mandatory human-review routing and qualified decision evidence.
- Fail-closed approval rules.
- Replay, concurrency, rollback, restart, and workspace isolation.
- Exactly-once Outbox.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec, and complete same-head CI.
