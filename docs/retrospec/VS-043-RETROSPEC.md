# VS-043 RetroSpec — Workflow catalog

## Delivered

A versioned repository-controlled workflow catalog with exact resolution, immutable definitions, dependency DAG validation, approved reference validation and deterministic SHA-256 fingerprinting.

## Corrections discovered by dual testing

- Equivalent reordered catalogs must preserve their fingerprint.
- Cycle-test fixtures must use otherwise valid identifiers so the intended graph rule is exercised.
- Continuity automation must be fail-closed and limited to scaffolding; it must never infer verification or merge authority.

## Durable rules

- Workflow IDs and versions resolve exactly.
- Unknown schema fields and unsupported versions are rejected.
- All dependency, tool-profile and model-role references are validated before exposure.
- Every dependency graph is acyclic.
- The next VS is selected only when its predecessor is explicitly complete in the status ledger.

Status: VERIFIED.
