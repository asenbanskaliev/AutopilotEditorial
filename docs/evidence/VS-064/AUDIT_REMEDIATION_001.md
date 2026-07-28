# VS-064 Audit Remediation 001

## Status

BLOCKED_PENDING_CODE_FIX.

A post-GREEN audit found four observable gaps that must be closed before merge:

1. Contradiction checks currently run during creation for every knowledge kind, incorrectly rejecting divergent beliefs. They must apply only to facts.
2. Contradictory facts can both be created as drafts and later activated. Fact contradiction and temporal overlap must be checked atomically during activation.
3. Create replay compares only a subset of immutable content. Evidence, normalized audiences, validity interval and attribution must participate in replay/conflict validation.
4. Disclosure mutates durable state but does not emit `editorial.knowledge-state.disclosed` through the transactional Outbox exactly once.

## Required regression coverage

- A belief may diverge from an active fact.
- Two overlapping contradictory facts cannot both become active.
- Reusing an entry identity with changed evidence, audience, validity or attribution fails closed.
- Disclosure replay creates exactly one disclosure and exactly one disclosure Outbox message.
- Failed activation or disclosure leaves no state or Outbox partial writes.

## Gate

Keep PR #99 in draft. After remediation, rerun Plan Integrity, Governance Gates and .NET CI on the exact corrected head. Merge is forbidden until all three conclude success and Auditoría M, Meta-Audit and RetroSpec are synchronized.
