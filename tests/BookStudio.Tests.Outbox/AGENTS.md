# Outbox Integration Test Instructions

## Allowed

- Real SQLite files in disposable workspaces.
- Deterministic clocks supplied explicitly to the store.
- Idempotency, lease, ownership, retry, reclaim and restart scenarios.
- References to Application contracts and Infrastructure implementations.

## Forbidden

- External brokers or network calls.
- Sleeps for lease timing; advance explicit timestamps instead.
- Mocks that bypass SQLite transactions.
- Treating at-least-once delivery as exactly-once processing.

The process must exit non-zero on any lifecycle or ownership regression.
