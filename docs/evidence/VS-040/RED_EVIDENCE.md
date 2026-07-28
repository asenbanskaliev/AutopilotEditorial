# VS-040 RED Evidence

## RED-I

Before this slice, the repository had a durable lease-based Outbox but no application contract that atomically committed Autopilot workflow state, idempotency receipt and Outbox envelopes in one transaction.

Missing at activation:

- transactional unit-of-work contract;
- SQLite workflow state and operation receipt migration;
- atomic implementation;
- idempotency conflict protection at operation level;
- cancellation/rollback proof;
- restart journey and final audit evidence.

## RED-E

The existing Outbox journey covered enqueue, claim, retry and reclaim, but did not prove mutation + Outbox atomicity. The new contractual journey is required to prove commit, rollback, replay, conflict, cancellation and restart.
