# VS-053 RED Evidence

Before this slice, an approved editorial proposal could not become a durable specification authority.

RED scenarios:

- No causal validation linked specification creation to an approved proposal revision.
- Prepare, commit and approve were not explicit fail-closed transitions.
- Committed content could not be proven immutable.
- Specification versions had no append-only history.
- Expected version and request replay conflicts were not rejected.
- Approval emitted no exactly-once authorization event for book planning.
- Restart durability for versions and approval evidence was unproven.

These scenarios are the independent failing baseline for VS-053.
