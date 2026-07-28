# VS-047 — Concurrency limits

## IntentSpec

Autopilot must bound parallel work by resource scope so multiple workers cannot exceed model, provider, workflow, project or global budgets, while preserving fairness, lease safety and restart recovery.

## BehaviorSpec

- Limits are versioned configuration keyed by scope type and scope key.
- Acquisition is atomic, lease-based and idempotent by request ID.
- A grant is issued only when every requested scope has available capacity.
- Multi-scope acquisition is all-or-nothing and ordered deterministically to avoid deadlocks.
- Renewal is restricted to the live lease owner.
- Release is idempotent and immediately restores capacity.
- Expired grants are reclaimable after a bounded sweep.
- Stale owners cannot renew or release a newer grant generation.
- Invalid, negative or over-capacity requests fail closed.
- Fairness is FIFO within equal priority; higher priority may proceed without violating capacity.
- Restart preserves configured limits, active grants and request receipts.
- No remote side effect occurs inside the persistence transaction.

## Core scopes

- `GLOBAL`
- `PROVIDER`
- `MODEL_ROLE`
- `WORKFLOW`
- `PROJECT`
- `TOOL_PROFILE`

## Gates

- `LIMIT_SCHEMA_PASS`
- `ATOMIC_MULTI_SCOPE_PASS`
- `CAPACITY_PASS`
- `LEASE_RENEW_PASS`
- `RELEASE_IDEMPOTENCY_PASS`
- `EXPIRY_RECLAIM_PASS`
- `STALE_OWNER_PASS`
- `FAIRNESS_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`
