# VS-064 GREEN Evidence

Functional remediation commit under test: `867c7b00b34033cfc14bf65bf40c00f518f53171`.

- `.NET CI` run `30400134791` / run number 802: PASS.
- Governance Gates run `30400134899` / run number 873: PASS.
- Plan Integrity run `30400134829` / run number 944: PASS.
- Cumulative Outbox journey includes `KNOWLEDGE_STATE_PASS`.

Verified after audit remediation:

- exact closed-transition authority;
- durable fact, belief and secret state with persisted attribution;
- divergent beliefs may coexist with facts;
- overlapping contradictory facts cannot both activate;
- create replay compares evidence, normalized audiences, validity, attribution and fingerprint;
- disclosure replay produces one durable disclosure and one `editorial.knowledge-state.disclosed` Outbox event;
- optimistic concurrency, rollback, restart durability and workspace isolation;
- exactly one `editorial.knowledge-state.activated` Outbox event.

Result: DUAL_GREEN and AUDIT_REMEDIATION_001_PASS.
