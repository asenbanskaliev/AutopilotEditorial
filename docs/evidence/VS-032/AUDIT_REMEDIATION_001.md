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

## Final evidence

Pending the full Plan Integrity, Governance and .NET CI rerun on the cleaned remediation head.
