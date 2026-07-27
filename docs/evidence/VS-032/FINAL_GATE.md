# VS-032 — Final Gate

VS-032 and its audit remediation may be considered complete only when the final audited branch head satisfies:

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ SSE_PARSE_PASS
∧ PROJECT_STREAM_PASS
∧ GLOBAL_STREAM_PASS
∧ RECONNECT_PASS
∧ POLL_RECONCILIATION_PASS
∧ DEDUPLICATION_PASS
∧ STATUS_CACHE_BOUNDED_PASS
∧ AUTH_AND_BOUNDARIES_PASS
∧ NO_MUTATION_PASS
∧ NO_LEAKED_TASKS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

## Required final properties

- Application remains provider-neutral.
- Compatibility is mandatory before stream or polling access.
- Only the five approved GET request patterns exist, including health and OpenAPI discovery.
- SSE parsing remains incremental, strict and bounded.
- Project requires `server.connected` as first dispatched data event.
- Global wrapper and directory remain bounded.
- Dedupe remains bounded and source-namespaced.
- Each status snapshot and the cross-snapshot status history are bounded.
- Status-cache updates do not consume extra slots.
- New insertion at capacity evicts the oldest remembered session deterministically.
- Re-observation after eviction is permitted and consumers remain idempotent.
- Snapshot absence never implies idle, deletion, completion or success.
- Reconnect remains iterative, bounded and cancellation-aware.
- Credentials, endpoint and provider bodies remain absent from events/evidence.
- Early disposal and cancellation await all owned tasks and close active connections.
- Every accumulated solution journey remains green.
- No temporary write-enabled workflow, migration script or trigger remains.

## Functional proof

```text
OPENCODE_SSE_RECONCILIATION_PASS scenarios=13 requests=57 events=34 gate=NO_MUTATION tasks=NO_LEAKED_TASKS
```

The final audited head must re-run Plan Integrity, Governance Gates and the complete .NET CI matrix after the audit, RetroSpec and evidence files are committed.

VS-033 may start only after PR #49 is merged. The full program remains `NOT_READY`.
