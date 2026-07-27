# VS-032 — GREEN Evidence

## Final functional contract

```text
OpenCode compatibility
→ bounded project/global SSE
→ strict normalization and deduplication
→ bounded cross-snapshot status history
→ bounded polling repair
→ monotonic provider-neutral events
→ deterministic cleanup
```

## Remediation functional head

```text
bdfd3f2c8ccf60631341845d8e384e19779f42ea
```

## Gates

- Plan Integrity: run `30303256533` — PASS.
- Governance Gates: run `30303256152` — PASS.
- Governance artifact: `8667391996`.
- Governance digest: `sha256:4404b720bf7d5344960aabdbf3cbe43ec148d83dca6cb8e7a666343f53ef16f5`.
- .NET CI: run `30303256913` — PASS.
- .NET job: `90101121629`.
- .NET artifact: `8667438911`.
- .NET digest: `sha256:8a992a6a7dedc5e4b3ce0e9c0c30b305fd143d063f14dbfd947db83c9176ab9f`.

## Reconciliation result

```text
OPENCODE_SSE_RECONCILIATION_PASS scenarios=13 requests=57 events=34 gate=NO_MUTATION tasks=NO_LEAKED_TASKS
```

Normalized contract:

- contract: `dotnet.opencode-sse-reconciliation-integration`;
- result: `PASS`;
- exit code: `0`;
- stdout SHA-256: `59e891a1e1ff1886cf618c29fbc179bfcc9f637b9849e6f74e8d8ebf915d09d9`;
- stderr SHA-256: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`;
- stderr: empty;
- retry chain: empty.

## Verified behavior

- provider-neutral Application boundary;
- health and required feature gate before streams;
- incremental strict UTF-8 SSE parsing;
- LF/CRLF, BOM, comments and multi-line data;
- bounded line, data, fields, IDs and status snapshots;
- project handshake and global wrapper;
- known and unknown status preservation;
- event-ID and payload-fingerprint deduplication;
- FIFO bounded cross-snapshot status history;
- deterministic eviction and later re-observation;
- EOF, malformed and stall reconciliation;
- bounded reconnect exhaustion;
- Basic auth without evidence leakage;
- optional session filtering;
- GET-only request inventory;
- caller cancellation and early enumerator disposal;
- zero active server connections after cleanup.

## Accumulated journeys

The complete solution, architecture fitness and every prior product, MCP and OpenCode journey remained green in `.NET CI` run `30303256913`.

## Audit synchronization

- `docs/audits/VS-032-M-AUDIT.md`: PASS.
- `docs/retrospec/VS-032-RETROSPEC.md`: original implemented contract.
- `docs/retrospec/VS-032-AUDIT-REMEDIATION-001.md`: bounded-history addendum.
- `docs/evidence/VS-032/AUDIT_REMEDIATION_001.md`: detailed finding and proof.
- Full program: `NOT_READY`.
