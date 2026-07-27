# VS-028 — GREEN Evidence

## Audited functional head

```text
2526487c3bf83ea44744d3fa235e94363864eab7
```

## Gates

- Plan Integrity: run `30269417984` — PASS.
- Governance Gates: run `30269418028` — PASS.
- Governance artifact: `8654148879`.
- Governance digest: `sha256:aa39f1a20ff9e462dfe36dd5e2aaf750ea8911c0157318b8ecd95ae47389e55b`.
- .NET CI: run `30269418098` — PASS.
- .NET job: `89987962976`.
- .NET artifact: `8654190966`.
- .NET digest: `sha256:989b3150c80227e9ba6db846f7ebeeb677d0868e90103b0feaf3d9790c1d4ade`.

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
- generic MCP conformance;
- MCP security sandbox.

Every normalized contract returned exit code 0.

## Security sandbox result

```text
MCP_SECURITY_SANDBOX_PASS servers=5 invalidStarts=25 policyReads=5 quotaChecks=5
```

Verified process behavior:

- five real bounded MCP executables;
- fail-closed filesystem-root and existing-file admission;
- invalid quota relationship and non-canonical number rejection;
- symlink-root rejection when supported by the runner;
- exact MCP 2025-11-25 initialize;
- complete cursor-based resource discovery;
- `book://security/sandbox-policy` present once per server;
- exact effective limits without physical path leakage;
- static policy read does not activate the workspace;
- clean EOF, exit 0, exhausted stdout and empty stderr.

Verified provider behavior:

- individual artifact byte limit;
- artifact-ID traversal rejection;
- exact file quota projection;
- exact byte quota projection;
- permanent usage excludes temp files;
- rejected writes publish no manifest or unreferenced new blob;
- rejected writes leave no temp files;
- rejected writes do not consume immutable versions;
- deduplicated content consumes one blob plus independent manifests.

## TestChangeRequest

- `TCR-028-001`: authorized the sandbox policy resource in all five cumulative resource catalogs.
- Fixed page-count expectations were replaced by complete `nextCursor` traversal.
- All previous schemas, profiles, prompts, order, invalid-cursor, tool, mutation, lazy-workspace and EOF checks remain required.

## Audit synchronization

- `docs/audits/VS-028-M-AUDIT.md`: PASS.
- `docs/retrospec/VS-028-RETROSPEC.md`: synced.
- `docs/evidence/VS-028/TEST_CHANGE_REQUEST.md`: APPROVED.
- Full program: `NOT_READY`.
