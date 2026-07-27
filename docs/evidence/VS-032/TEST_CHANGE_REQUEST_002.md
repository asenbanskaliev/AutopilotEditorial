# VS-032 — TestChangeRequest TCR-032-002

## Trigger

`EofReconnectAndPollingAsync` stopped enumeration as soon as the poll snapshot changed from `busy` to `idle`. The reconciler correctly schedules polling immediately after EOF, so that synthetic status can arrive before the next TCP connection is accepted. The subsequent assertion then observed `streamCalls == 1` even though reconnect was already scheduled.

## Approved change

The scenario completion predicate must wait for both:

```text
poll repair emitted idle
AND
second project stream connection accepted
```

## Preserved requirements

- EOF must still emit an `eof` reconciliation event;
- polling must still emit initial `busy` and repaired `idle` states;
- at least two project stream connections remain mandatory;
- at least two status polls remain mandatory;
- reconnect delay, cancellation and no-leaked-task checks remain unchanged;
- no product behavior or observable requirement is relaxed.

## Test Auditor decision

**APPROVED** — the test now waits for the two independently asynchronous outcomes it already requires instead of terminating after only the first one.
