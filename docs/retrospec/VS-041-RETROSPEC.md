# VS-041 RetroSpec — Scheduler and jobs

## Delivered

A durable SQLite scheduler with versioned jobs, deterministic priority ordering, delayed availability, ownership-safe leases, renewal, completion, retry and restart reclaim.

## Corrections discovered by dual testing

The original journey advanced simulated time to +30 minutes before checking a retry scheduled at +10 minutes, so the retry was correctly claimable and the assertion was invalid. The journey now keeps time monotonic: pre-retry check at +9, retry at +10, pre-future check at +30 and scheduled future execution at +60.

## Durable rules

- Tests must preserve monotonic simulated time.
- Higher priority never bypasses `AvailableAtUtc`.
- Only a live lease owner may mutate a running job.
- Completion is terminal; failures remain retryable.
- Expired running jobs may be reclaimed after restart.
- Handlers must be idempotent under at-least-once execution.

Status: VERIFIED.
