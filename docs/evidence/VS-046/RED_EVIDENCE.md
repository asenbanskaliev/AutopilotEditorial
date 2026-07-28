# VS-046 RED Evidence

Before this slice the system had retrying scheduler jobs and durable Outbox delivery, but no first-class quarantine or controlled recovery model after retry exhaustion.

RED scenarios:

- Exhausted failures could not be preserved as immutable dead-letter evidence.
- Duplicate capture had no immutable-fingerprint conflict detection.
- Operators could not repair payload/schema under an auditable request identity.
- Requeue identity was not deterministic and could duplicate downstream work.
- Discard semantics were not terminal or attributable.
- Restart durability for recovery state was unproven.

These scenarios define the independent failing baseline for VS-046.
