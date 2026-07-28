# VS-043 — Workflow catalog

## IntentSpec

Autopilot workflows must be repository-controlled, versioned, immutable and fully validated before any jobs are created.

## BehaviorSpec

- Workflow identity is exact `workflowId + version`.
- Every step has immutable ID, job type/version, tool profile, model role, timeout, max attempts and dependency list.
- Duplicate workflows or steps fail closed.
- Dependencies must reference steps in the same workflow.
- Self-dependencies and dependency cycles are rejected.
- Tool-profile and model-role references must exist in caller-supplied approved sets.
- Resolution never falls back across workflow versions.
- Catalog and nested collections are immutable.
- Canonical SHA-256 fingerprint is independent of input ordering.
- Repository JSON is bounded and rejects unknown properties.

## TDD Dual

- RED-I: workflow contracts, catalog, loader and repository catalog absent.
- RED-E: no exact-version, cycle, reference or fingerprint journey.
- GREEN-I: build, architecture and governance pass.
- GREEN-E: cumulative workflow-catalog journey passes.

## Gates

- `CATALOG_SCHEMA_PASS`
- `REFERENCE_INTEGRITY_PASS`
- `ACYCLIC_PASS`
- `EXACT_VERSION_PASS`
- `FINGERPRINT_PASS`
- `IMMUTABLE_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`
