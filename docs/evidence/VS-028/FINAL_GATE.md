# VS-028 — Final Gate

The slice may merge only when the audited branch head satisfies:

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ PATH_SANDBOX_PASS
∧ FILE_QUOTA_PASS
∧ POLICY_CONFORMANCE_PASS
∧ MCP_CONFORMANCE_PASS
∧ NO_ORPHANS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

The final check must re-run Plan Integrity, Governance Gates and the complete .NET CI matrix after audit, retrospec, execution status and evidence documents are committed.

Required final properties:

- all five MCP processes enforce the same admitted workspace and effective limits;
- the policy resource is path-free, unique and readable without activating the workspace;
- quota rejection is transactional and preserves immutable versions;
- all previous MCP and product journeys remain green;
- no temporary migration workflow or script remains in the repository.

`VS-028` closes the planned F2-MCP slice sequence. The full program remains `NOT_READY`.
