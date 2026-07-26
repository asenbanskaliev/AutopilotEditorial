# Worker Host Instructions

## Allowed

- Claim, heartbeat, timeout, retry and dead-letter execution.
- Compose Autopilot, Infrastructure and OpenCode adapters.
- Emit traces, metrics and normalized operational errors.
- Graceful shutdown and cancellation.

## Forbidden

- Domain or editorial policy decisions.
- Direct mutation of canonical state outside Application use cases.
- Unbounded concurrency or retries.
- Interactive questions inside autonomous jobs.
- Storing workflow truth in process memory.

Every job handler must be idempotent and recoverable after process termination.
