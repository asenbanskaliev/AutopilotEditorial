# VS-032 — RetroSpec Audit Remediation 001

## Reason for the addendum

The original VS-032 contract bounded each `/session/status` response, but its in-watch history retained every distinct session ID seen across SSE and later snapshots. A long-lived watch could therefore grow independently of the configured snapshot limit.

This addendum supersedes the original assumption that snapshot bounds alone also bounded cumulative status memory.

## Implemented contract

Each watch now owns:

```text
OpenCodeBoundedStatusCache(capacity = MaximumStatusEntries)
```

The cache is process-local and FIFO.

Rules:

- a new session is appended to insertion order;
- updating an existing session changes its value without adding another queue entry;
- adding a new session at capacity evicts the oldest remembered session;
- dictionary and queue remain bounded by the same configured capacity;
- an evicted session may be emitted again when later observed;
- absence from a snapshot still does not imply idle, deletion, completion or success.

## Reconciliation behavior

```text
provider session.status
→ deduplicate provider event
→ boundedCache.Set(sessionId, status)
→ emit provider-neutral event

poll snapshot entry
→ boundedCache.TryGet(sessionId)
→ suppress only when remembered value is equal
→ boundedCache.Set(sessionId, status)
→ emit synthetic status when new, changed or previously evicted
```

## Executable proof

`StatusCacheBoundedAsync` uses the real loopback SSE server and the real reconciler:

1. configure `MaximumStatusEntries = 2`;
2. observe `ses_cache_a`, `ses_cache_b` and `ses_cache_c` through project SSE;
3. FIFO insertion evicts `ses_cache_a`;
4. EOF triggers `/session/status` polling;
5. polling returns unchanged `ses_cache_a=busy`;
6. a synthetic event is emitted because the session was previously evicted;
7. all requests remain GET and all connections close.

Verified output:

```text
OPENCODE_SSE_RECONCILIATION_PASS scenarios=13 requests=57 events=34 gate=NO_MUTATION tasks=NO_LEAKED_TASKS
```

## Operational consequence

The implementation chooses bounded memory over indefinite suppression history. Re-emission after eviction is expected and safe; consumers must remain idempotent and must not interpret duplicate current-state observations as a new provider transition.

## Residual constraints

- cache contents are not durable across process restart;
- FIFO is deterministic but not LRU;
- no durable event offset is introduced;
- polling still provides current observable state rather than every missed intermediate event;
- completion logic remains outside the reconciler.

## Evidence

- Issue: `#48`.
- Remediation PR: `#49`.
- Functional head: `bdfd3f2c8ccf60631341845d8e384e19779f42ea`.
- Plan Integrity: `30303256533` — PASS.
- Governance: `30303256152` — PASS.
- .NET CI: `30303256913` — PASS.
- Detailed evidence: `docs/evidence/VS-032/AUDIT_REMEDIATION_001.md`.

## Phase result

The cumulative status history is now bounded and VS-032 again satisfies `M_AUDIT_PASS`. VS-033 remains blocked until PR #49 is merged.
