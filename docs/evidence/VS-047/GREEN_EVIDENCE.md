# VS-047 GREEN Evidence

Status: DUAL_GREEN

- Versioned concurrency limit configuration: PASS.
- Atomic multi-scope acquisition: PASS.
- Global/provider hierarchy enforcement: PASS.
- Capacity denial without partial reservation: PASS.
- Idempotent acquire and release replay: PASS.
- Lease renewal with generation fencing: PASS.
- Stale worker rejection: PASS.
- Expired lease reclamation: PASS.
- Restart durability and capacity recovery: PASS.
- No remote mutation in the persistence transaction: PASS.

Marker:

`CONCURRENCY_LIMITS_PASS schema=PASS atomic=PASS hierarchy=PASS capacity=PASS idempotency=PASS renew=PASS release=PASS stale_worker=PASS reclaim=PASS restart=PASS mutation=NONE`
