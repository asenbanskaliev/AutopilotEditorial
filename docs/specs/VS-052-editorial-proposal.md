# VS-052 — Editorial proposal

## IntentSpec

A completed discovery session must be transformed into an explicit, reviewable editorial proposal before specification work begins.

## BehaviorSpec

- A proposal belongs to one workspace, project and completed discovery session.
- Proposal identity and revision are stable and durable.
- Content includes premise, audience, promise, scope, differentiators, risks, assumptions, success criteria and recommended next step.
- Evidence references link proposal claims back to discovery answers and decisions.
- Draft revisions append immutable history instead of overwriting approved evidence.
- Submit validates required sections and freezes the submitted revision.
- Approval and rejection are attributable, reasoned and idempotent.
- Only an approved proposal can authorize VS-053 specification lifecycle.
- Conflicting request-ID reuse fails closed.
- Approval emits exactly one transactional Outbox event `editorial.proposal.approved`.
- Restart preserves revisions, review decision and pending delivery intent.
- No remote mutation occurs inside the persistence transaction.

## States

`DRAFT → SUBMITTED → APPROVED`

`SUBMITTED → REJECTED`

A rejected proposal requires a new draft revision before resubmission.

## Gates

- `PROPOSAL_SCHEMA_PASS`
- `DISCOVERY_LINK_PASS`
- `REVISION_HISTORY_PASS`
- `SUBMISSION_GATE_PASS`
- `APPROVAL_PASS`
- `REJECTION_PASS`
- `IDEMPOTENCY_PASS`
- `OUTBOX_ONCE_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`
