# VS-030 — Final Gate

The slice may merge only when the audited branch head satisfies:

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ OPENCODE_HEALTH_PASS
∧ FEATURE_DETECTION_PASS
∧ AUTH_AND_BOUNDARIES_PASS
∧ NO_SIDE_EFFECT_DISCOVERY_PASS
∧ NO_ORPHANS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

The final check must re-run Plan Integrity, Governance Gates and the complete .NET CI matrix after audit, RetroSpec, execution status and evidence documents are committed.

Required final properties:

- Application remains provider-neutral;
- endpoint validation remains fail-closed;
- HTTP remains loopback-only;
- compatibility discovery emits only GET requests;
- no sessions, prompts, models or mutations are executed;
- health, version and feature compatibility remain separate facts;
- health evidence remains tri-state and evidence-based;
- response bodies, URLs and credentials remain absent from reports and evidence;
- all previous product, MCP conformance and sandbox journeys remain green;
- no temporary workflow, migration script or trigger marker remains in the repository.

`VS-030` establishes the compatibility prerequisite for `VS-031 — Session lifecycle`. The full program remains `NOT_READY`.
