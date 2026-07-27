# VS-031 — Final Gate

The slice may merge only when the final audited branch head satisfies:

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ SESSION_CREATE_PASS
∧ ASYNC_PROMPT_PASS
∧ SESSION_STATUS_PASS
∧ SESSION_ABORT_PASS
∧ IDEMPOTENCY_PASS
∧ AUTH_AND_BOUNDARIES_PASS
∧ NO_UNPLANNED_MUTATION_PASS
∧ NO_ORPHANS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

The final head must re-run Plan Integrity, Governance Gates and the complete .NET CI matrix after audit, RetroSpec, evidence and execution status are committed.

Required final properties:

- Application remains provider-neutral;
- VS-030 compatibility remains mandatory before session mutation;
- invalid input reaches neither compatibility nor HTTP;
- create and prompt retain bounded local idempotency;
- same key/different fingerprint fails before HTTP;
- failed/cancelled reservation remains retryable;
- prompt acceptance is not interpreted as completion;
- unknown statuses are not interpreted as idle;
- abort remains explicit;
- only the seven allowed compatibility/lifecycle request patterns exist;
- credentials, endpoint, prompt and bodies remain absent from errors/evidence;
- all accumulated product, MCP and OpenCode compatibility journeys remain green;
- no temporary workflow, migration script or trigger marker remains.

`VS-031` establishes the lifecycle prerequisite for `VS-032 — SSE reconciliation`. The full program remains `NOT_READY`.
