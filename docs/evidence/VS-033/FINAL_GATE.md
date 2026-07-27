# VS-033 — Final Gate

VS-033 is complete only when the final audited branch head satisfies:

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ PROFILE_SCHEMA_PASS
∧ DENY_BY_DEFAULT_PASS
∧ WORKFLOW_RESOLUTION_PASS
∧ UNKNOWN_TOOL_REJECTED_PASS
∧ PRIVILEGE_ESCALATION_BLOCKED_PASS
∧ PROVIDER_NEUTRAL_PASS
∧ NO_MUTATION_OUTSIDE_PROFILE_PASS
∧ DUAL_GREEN
∧ M_AUDIT_PASS
∧ META_AUDIT_PASS
∧ RETROSPEC_PASS
```

## Required final properties

- Application contracts contain no provider/HTTP/JSON DOM dependencies.
- Catalog/profile versions are immutable after load and bounded.
- Exact matching is used for profile, version, workflow, role, capability and tool.
- Deny overrides allow and absence means denied.
- Unknown capabilities/tools fail before mapping.
- Child effective policy is a subset of its parent.
- Human approval cannot be disabled downstream.
- Tool-call and parallelism limits only decrease downstream.
- Effective equality and SHA-256 fingerprinting are deterministic.
- Effective and mapped constructors are not public trust bypasses.
- Fingerprints are audit hashes, not signatures or bearer authorization.
- Provider support cannot broaden permissions.
- Unsupported deny semantics or tools fail closed.
- Resolution and mapping perform no provider/session mutation.
- Error evidence does not echo rejected values, prompts or credentials.
- Concurrent resolution is deterministic and cancellation-aware.
- Architecture, solution, CI provider and workflow registration are present.
- Workflow uses `contents: read` and no temporary scripts remain.
- Every accumulated solution journey remains green.

## Executable marker

```text
OPENCODE_AGENT_TOOL_PROFILES_PASS scenarios=12 profiles=5 fingerprints=6 gate=NO_PRIVILEGE_ESCALATION mutation=NONE
```

The branch must run Plan Integrity, Governance Gates and the complete .NET CI matrix after the audit, RetroSpec, evidence and trust-boundary hardening are committed.

VS-034 may start only after PR #50 is merged. The full program remains `NOT_READY`.
