# API Integration Test Instructions

## Allowed

- Start real Kestrel hosts on ephemeral loopback ports.
- Use disposable workspace roots and explicit configuration.
- Test live, ready, diagnostics, correlation, Problem Details and shutdown.
- Inspect responses for sensitive-data disclosure.

## Forbidden

- External network calls or non-loopback binds.
- Mock servers that bypass the Control Center composition root.
- Fixed ports, sleeps or reliance on machine-specific paths.
- Returning raw exception messages, stack traces, connection strings or workspace paths.

Every host must stop and dispose in `finally`, and the process must exit non-zero on any regression.
