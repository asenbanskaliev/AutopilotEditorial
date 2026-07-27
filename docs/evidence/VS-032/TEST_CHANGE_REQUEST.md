# VS-032 — TestChangeRequest TCR-032-001

## Trigger

The first permanently wired GREEN reached build and architecture, while Governance found static ownership mismatches:

1. the executable success marker `OPENCODE_SSE_RECONCILIATION_PASS` is emitted by `Program.cs`, not by the journey class;
2. Basic Authorization is asserted in `OpenCodeSseReconciliationJourney`, while the generic socket server intentionally records all headers without embedding authentication policy.

## Approved change

- keep all twelve scenario-name, `NO_MUTATION` and `NO_LEAKED_TASKS` checks in the journey;
- assert `OPENCODE_SSE_RECONCILIATION_PASS` in `Program.cs`;
- keep `TcpListener`, SSE media type, content length, request recording and active-connection checks in the server;
- assert `Authorization` in the journey where authenticated compatibility, stream and polling requests are exercised.

## Preserved requirements

- the real adapter and real loopback socket server remain mandatory;
- the success marker remains required from the executable;
- Basic auth must remain present on health, OpenAPI, SSE and status polling requests;
- credentials and Authorization values must not leak into normalized events;
- no parser, reconnect, polling, dedupe, filtering, cancellation, GET-only or task-cleanup scenario is removed;
- no observable expectation is relaxed.

## Test Auditor decision

**APPROVED** — static checks move to the files that own the behavior while the executable journey remains unchanged and mandatory.
