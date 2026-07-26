# Observability Integration Test Instructions

## Allowed

- Start real Kestrel hosts on ephemeral loopback ports.
- Emit traces, metrics and structured logs through the real OpenTelemetry SDK.
- Force-flush providers and inspect the bounded sanitized snapshot.
- Use fixed synthetic secrets only to prove redaction.
- Validate OTLP options without contacting an external collector.

## Forbidden

- External network calls or non-loopback binds.
- Mock telemetry providers that bypass the application composition root.
- Fixed ports, sleeps or dependence on machine-specific paths.
- Persisting telemetry outside disposable test roots.
- Accepting raw messages, stack traces, request bodies, prompts, paths or credentials in snapshots.

Every host must stop and dispose in `finally`, and the process must exit non-zero on any regression.
