# VS-026 — Final Gate

The slice may merge only when the audited branch head satisfies:

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ MCP_PROMPTS_CONFORMANCE_PASS
∧ RESOURCE_PARITY_PASS
∧ NO_ORPHANS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

The final check must re-run Plan Integrity, Governance Gates and the complete .NET CI matrix after the audit, retrospec, execution status and evidence documents are committed.

The full program remains `NOT_READY` after this slice.
