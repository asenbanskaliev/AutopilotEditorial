# VS-052 RED Evidence

Before this slice, completed discovery evidence could not be converted into a durable editorial proposal with controlled revisions and explicit approval.

RED scenarios:

- No stable proposal identity linked project and completed discovery.
- Proposal revisions could overwrite prior evidence instead of appending history.
- Required proposal sections had no fail-closed submission gate.
- Approval and rejection lacked attributable, idempotent decisions.
- Conflicting expected revisions were not rejected.
- Approved proposals emitted no exactly-once authorization event.
- Restart durability for proposal review state was unproven.

These scenarios define the independent failing baseline for VS-052.
