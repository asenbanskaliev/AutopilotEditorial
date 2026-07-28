# VS-053 — Specification lifecycle

## IntentSpec

Transform an approved editorial proposal into a durable, versioned and reviewable book specification that becomes the immutable authority for planning only after explicit approval.

## BehaviorSpec

- A specification is created only from an `APPROVED` editorial proposal in the same workspace and project.
- Proposal identity, approved revision and approval message ID are preserved as causal evidence.
- Each specification version follows `DRAFT → PREPARED → COMMITTED → APPROVED`.
- Draft edits append a new revision of the active version; committed content is immutable.
- Prepare validates goals, audience, scope, constraints, quality bars, deliverables and acceptance criteria.
- Commit records a deterministic content digest and freezes the version.
- Approval is attributable, reasoned and idempotent.
- Approval emits exactly one transactional Outbox event `editorial.specification.approved`.
- A new version may be opened only from an approved current version and never mutates prior versions.
- Optimistic expected-version checks and conflicting request-ID reuse fail closed.
- Restart preserves all versions, approval evidence and pending Outbox intent.
- No remote mutation occurs inside the persistence transaction.

## Gates

- `SPEC_SCHEMA_PASS`
- `APPROVED_PROPOSAL_LINK_PASS`
- `PREPARE_VALIDATION_PASS`
- `COMMIT_IMMUTABILITY_PASS`
- `APPROVAL_PASS`
- `VERSION_HISTORY_PASS`
- `IDEMPOTENCY_PASS`
- `OUTBOX_ONCE_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`
