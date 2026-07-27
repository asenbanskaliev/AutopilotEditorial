# VS-032 — Audit Remediation 001

"
    "## Finding

"
    "M4 detected that each `/session/status` snapshot was bounded, but the cross-snapshot session-status history used an ordinary dictionary and could accumulate distinct session IDs for the lifetime of a watch.

"
    "## Correction

"
    "- replace the unbounded dictionary with a FIFO `OpenCodeBoundedStatusCache`;
"
    "- capacity is exactly `MaximumStatusEntries`;
"
    "- updates do not consume extra slots;
"
    "- insertion at capacity evicts the oldest remembered session;
"
    "- absence from a snapshot still does not imply idle, deletion or completion.

"
    "## Executable proof

"
    "`StatusCacheBoundedAsync` uses capacity 2, observes three SSE session IDs and then polls the first unchanged status after EOF. Re-emission proves deterministic eviction instead of unbounded retention.

"
    "## Classification

"
    "Product and test strengthening. No existing observable guarantee was removed.
"
    