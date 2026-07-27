# VS-030 — GREEN Evidence

## Audited functional head

```text
8cde1f9ea94bf6ceb6480c978f615816c9b193a0
```

## Gates

- Plan Integrity: run `30284506174` — PASS.
- Governance Gates: run `30284506511` — PASS.
- Governance artifact: `8660200965`.
- Governance digest: `sha256:1e349e360e8e840ae03b019cba0b364fd9eb5a545b2bdf788299348975f6dfc2`.
- .NET CI: run `30284506526` — PASS.
- .NET job: `90038837852`.
- .NET artifact: `8660239416`.
- .NET digest: `sha256:5b8fc6da96e78e36895e90bdbba9a213f8ff1418db28564aa17a4a6d948adb5f`.

## Verified accumulated journeys

- solution build;
- architecture fitness;
- SQLite;
- Artifact Store;
- Outbox;
- API and Control Center;
- OpenTelemetry;
- MCP initialize;
- book-core;
- book-authoring;
- book-quality;
- book-production;
- book-ops;
- prompts/resources;
- MCP conformance;
- MCP security sandbox;
- OpenCode compatibility.

Every normalized contract returned exit code 0.

## OpenCode compatibility result

```text
OPENCODE_COMPATIBILITY_PASS scenarios=13 requests=18 features=12
```

Normalized contract:

- result: `PASS`;
- exit code: `0`;
- stderr: empty;
- stdout SHA-256: `1076a1d25f22ddd1fdc8fce307f8a7a3017cbd367852d6abd5fb21931b921cb4`;
- stderr SHA-256: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.

## Verified product behavior

- provider-neutral Application contract;
- safe endpoint configuration;
- HTTP restricted to loopback;
- HTTPS accepted;
- no credentials in URL;
- optional Basic auth;
- no credential or response-body leakage;
- health and version parsed independently;
- bounded response streams;
- no automatic redirects;
- no automatic retries;
- cancellation propagation;
- stable timeout/connection/status/payload codes;
- OpenAPI 3.x path inspection;
- normalized session templates;
- exact 12-feature catalog;
- sorted detected and missing sets;
- maximum two requests per probe;
- only GET requests;
- no session, prompt, model or remote mutation;
- tri-state `healthy=true|false|unknown` evidence.

## Scenario matrix

1. compatible server;
2. missing required feature;
3. unhealthy server;
4. authentication required;
5. authenticated compatible server;
6. malformed health;
7. invalid OpenAPI;
8. HTML documentation;
9. oversized health;
10. oversized OpenAPI;
11. timeout;
12. external cancellation;
13. endpoint and bounds validation.

## TestChangeRequests

- `TCR-030-001`: strengthened typed scheme-token assertions.
- `TCR-030-002`: corrected health evidence to tri-state and added cumulative assertions.
- No scenario, request-count assertion, auth check, bound, feature or no-side-effect requirement was removed.

## Audit synchronization

- `docs/audits/VS-030-M-AUDIT.md`: PASS.
- `docs/retrospec/VS-030-RETROSPEC.md`: synced.
- `docs/evidence/VS-030/TEST_CHANGE_REQUEST.md`: APPROVED.
- Full program: `NOT_READY`.
