# VS-032 — Audit Remediation 001

## Finding

M4 detected that each `/session/status` snapshot was bounded, but the cross-snapshot session-status history used an ordinary dictionary and could accumulate distinct session IDs for the lifetime of a watch.

## Correction

- Replace the unbounded dictionary with a FIFO `OpenCodeBoundedStatusCache`.
- Capacity is exactly `MaximumStatusEntries`.
- Updates to an existing session do not consume additional slots.
- Insertion at capacity evicts the oldest remembered session deterministically.
- Absence from a snapshot still does not imply idle, deletion or completion.

## Executable proof

`StatusCacheBoundedAsync` configures capacity 2, observes three different session IDs through the real project SSE stream, and then polls the first unchanged status after EOF. Its synthetic re-emission proves that the oldest entry was evicted and re-observed instead of being retained by an unbounded history.

The scenario also preserves the existing gates:

```text
GET_ONLY
NO_MUTATION
NO_LEAKED_TASKS
```

## Classification

Product and test strengthening. No existing observable guarantee was removed or relaxed.

## GREEN evidence

Functional remediation head:

```text
bdfd3f2c8ccf60631341845d8e384e19779f42ea
```

Gates:

- Plan Integrity: run `30303256533` — PASS.
- Governance Gates: run `30303256152` — PASS.
- Governance artifact: `8667391996`.
- Governance digest: `sha256:4404b720bf7d5344960aabdbf3cbe43ec148d83dca6cb8e7a666343f53ef16f5`.
- .NET CI: run `30303256913` — PASS.
- .NET job: `90101121629`.
- .NET artifact: `8667438911`.
- .NET digest: `sha256:8a992a6a7dedc5e4b3ce0e9c0c30b305fd143d063f14dbfd947db83c9176ab9f`.

Normalized result:

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
