# VS-027 — GREEN Evidence

## Functional head

```text
779e86714e5641b92dacfd1985e5130b1c6411a2
```

## Gates

- Plan Integrity: run `30262310946` — PASS.
- Governance Gates: run `30262310999` — PASS.
- Governance artifact: `8651376125`.
- Governance digest: `sha256:b0bc32013ee25f48c125643b2af0d5b23e272b15d0de860420d72766ac06f5a4`.
- .NET CI: run `30262310959` — PASS.
- .NET job: `89964849538`.
- .NET artifact: `8651404980`.
- .NET digest: `sha256:a51473caa5e4960d8a0f545ea1f060f4579816867b16530ad0235355acc0a735`.

## Conformance result

```text
MCP_CONFORMANCE_PASS servers=5 corpus=27 fuzz=640 seed=27027 sha256=2af65427878c95b3d582413703247f46828debca5d694f0a456ef0a65b61d4b2
```

- result: PASS;
- exit code: 0;
- stderr: empty;
- duration: 1758 ms in normalized evidence;
- stdout SHA-256: `2a2a6b722f86de54e46728d0ae88d9b71f4f64f24d978c687a6787116d18783c`.

## Verified properties

- five real MCP child processes;
- JSON-RPC malformed-input corpus;
- created and ready lifecycle phases;
- exact protocol, identity and capabilities;
- 128 deterministic generated cases per server;
- survival ping every 16 cases;
- depth and 1 MiB transport limits;
- duplicate request ID behavior;
- unknown notification isolation;
- no canary/path/database leak;
- no workspace creation;
- no crash or hang;
- clean EOF and no extra stdout.

## Regression matrix

All prior journeys remained PASS:

- architecture;
- SQLite;
- Artifact Store;
- Outbox;
- API/Control Center;
- OpenTelemetry;
- MCP initialize;
- book-core;
- book-authoring;
- book-quality;
- book-production;
- book-ops;
- prompts/resources.

## Audit synchronization

- `docs/audits/VS-027-M-AUDIT.md`: PASS.
- `docs/retrospec/VS-027-RETROSPEC.md`: synced.
- `TCR-027-001`: approved.
- Full program: NOT_READY.
