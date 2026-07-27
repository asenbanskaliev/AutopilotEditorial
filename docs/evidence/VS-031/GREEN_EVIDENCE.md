# VS-031 — GREEN Evidence

## Functional head

```text
e0c26119ac48d94d98bbbb61bbf4ad3a9cc51b8c
```

## Gates

- Plan Integrity: run `30288498922` — PASS.
- Governance Gates: run `30288498578` — PASS.
- Governance artifact: `8661799205`.
- Governance digest: `sha256:b74f55a7fea3e58676a8a537eb5b85d76f1e48ebe2bbb13b0148b93baea75006`.
- .NET CI: run `30288498624` — PASS.
- .NET job: `90052129510`.
- .NET artifact: `8661833536`.
- .NET digest: `sha256:919eed4f20795219e06be3e26681298a58ba954eff06f7b96e3e679a772f1a71`.

## Session lifecycle result

```text
OPENCODE_SESSION_LIFECYCLE_PASS scenarios=19 requests=50 mutations=15 gate=NO_UNPLANNED_MUTATION
```

Normalized contract:

- contract: `dotnet.opencode-session-lifecycle-integration`;
- result: `PASS`;
- exit code: `0`;
- stdout SHA-256: `7b185c270768a700acf66ae51c2233640e4e444aa2ced7ea0f51ff9a1c895ebf`;
- stderr SHA-256: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`;
- stderr: empty;
- retry chain: empty.

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
- OpenCode compatibility;
- OpenCode session lifecycle.

Every normalized contract returned exit code 0.

## Verified lifecycle behavior

- provider-neutral Application port;
- bounded input validation before compatibility/HTTP;
- compatibility gate requires health and five session features;
- compatibility refusal emits no mutation;
- create/get session mapping;
- create same-key replay and conflict;
- concurrent duplicate create emits one POST;
- async text prompt exact HTTP 204;
- prompt replay and conflict;
- sorted idle/busy/retry/unknown statuses;
- explicit abort true and false;
- Basic auth on compatibility and lifecycle requests;
- no credential/body/prompt leakage;
- response size rejection;
- malformed session/status/abort rejection;
- timeout mapping;
- caller cancellation propagation;
- failed reservation release and later retry;
- exact inventory of 50 requests;
- 15 planned mutations only;
- no delete, patch, shell, command, share or file operations.

## TestChangeRequest

`TCR-031-001` strengthened ownership precision in static tests. The real HTTP journey and all observable assertions were unchanged.

## Audit synchronization

- `docs/audits/VS-031-M-AUDIT.md`: PASS.
- `docs/retrospec/VS-031-RETROSPEC.md`: synced.
- `docs/evidence/VS-031/TEST_CHANGE_REQUEST.md`: APPROVED.
- Full program: `NOT_READY`.
