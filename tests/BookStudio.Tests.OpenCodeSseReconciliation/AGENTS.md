# BookStudio.Tests.OpenCodeSseReconciliation Agent Rules

## Allowed

- Run loopback-only contractual HTTP/SSE servers owned by the test process.
- Exercise the real `BookStudio.OpenCode` parser, normalizer, deduplicator and reconciler.
- Verify project/global streams, polling repair, reconnect/backoff, authentication, bounds and cancellation.
- Record every HTTP method/path/header required to prove GET-only behavior.
- Use deterministic clocks only for assertions that do not depend on wall-clock precision.

## Forbidden

- Do not contact public OpenCode servers or external networks.
- Do not create sessions, send prompts, abort, invoke models or mutate provider configuration.
- Do not mock `IOpenCodeEventReconciler`, the SSE parser or the status polling transport.
- Do not print credentials, Authorization headers, endpoint URLs or event bodies.
- Do not leave server connections, enumerators or background tasks running after a scenario.
