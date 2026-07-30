# VS-090 — RED Evidence

## RED-I

The repository does not yet expose Application contracts for durable research planning authorized by an exact approved VS-088 review.

Expected missing capability:

- create and load a research plan;
- define typed and prioritized research questions;
- define source strategies and evidence expectations;
- approve, block, or mark the plan stale;
- enforce exact replay, optimistic concurrency, rollback, restart, workspace isolation, append-only history, and Outbox exactly-once.

Result: RED by intentional absence of implementation.

## RED-E

The cumulative executable journey does not yet prove:

- exact authority from VS-088;
- dependency-ready transition into research planning;
- rejection of stale authority and incomplete evidence;
- idempotent replay and conflicting replay failure;
- transaction rollback and recovery after restart;
- workspace isolation and exactly-once event publication.

Result: RED until the VS-090 journey and implementation are added and pass in CI.
