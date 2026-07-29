# VS-067 RED Evidence

## RED-I — implementation gap

At slice start the repository can detect coherence findings and maintain durable knowledge, state, timeline and plot threads, but it cannot express or apply a localized repair as a governed aggregate.

Missing executable behaviors:

- no repair proposal identity, lifecycle or immutable scope;
- no exact authority binding from finding/audit to patch;
- no expected version/digest preconditions or drift detection;
- no typed localized operations;
- no atomic update of target and dependent projections;
- no pre/post coherence validation;
- no append-only repair history or recoverable previous version;
- no strict replay by canonical payload;
- no exactly-once repair outcome events;
- no restart, rollback and workspace-isolation journey.

## RED-E — expected failures

The cumulative journey must initially fail because no repair contracts, migration or SQLite store exist. Tests will require:

1. exact replay without duplicate mutation;
2. conflicting request/payload rejection;
3. stale digest/version rejection with zero writes;
4. scope escape rejection;
5. blocking post-validation rollback;
6. atomic target and projection update;
7. previous-version recoverability;
8. restart durability and workspace isolation;
9. exactly one Outbox outcome event.

VS-067 remains RED until contracts, migration, transactional store and cumulative journey implement these behaviors.
