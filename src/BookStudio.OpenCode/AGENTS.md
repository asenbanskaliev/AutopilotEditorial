# OpenCode Adapter Instructions

## Allowed

- Health and compatibility detection.
- Session, prompt, message, event and abort transport.
- Mapping provider responses to Application contracts.
- Timeouts, retries and response-size limits.
- Reference to Application only.

## Forbidden

- Editorial decisions, workflow planning or memory ownership.
- Direct database or filesystem writes.
- Leaking provider-specific DTOs into Application.
- Assuming capabilities without runtime detection.
- Reading provider credentials from committed files.

All network behavior requires fake-server contract tests and cancellation coverage.
