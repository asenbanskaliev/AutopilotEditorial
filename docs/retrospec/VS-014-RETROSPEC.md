# VS-014 — RetroSpec

## Implemented contract

The canonical VS-014 now provides a secure local HTTP foundation for the Control Center.

## Binding contract

- Default URL: `http://127.0.0.1:5074`.
- HTTP/HTTPS absolute URLs only.
- Non-loopback binding is rejected unless `ControlCenter:AllowRemoteBinding=true`.
- Tests and automation may use loopback port zero.

## Endpoint contract

- `GET /health/live`: process liveness, independent of storage.
- `GET /health`: compatibility alias for liveness.
- `GET /health/ready`: required probe state with 200/503.
- `GET /api/v1/diagnostics`: version, environment, uptime and sanitized readiness summaries.
- Unknown routes produce `application/problem+json`.

## Correlation contract

- Header: `X-Correlation-ID`.
- Safe incoming values are preserved.
- Missing, control-containing or longer-than-128 values are replaced with a generated opaque ID.
- The response header and Problem Details carry the selected ID.

## Readiness contract

- `IReadinessProbe` is provider-neutral.
- `WorkspaceDatabaseReadinessProbe` maps durable health into `ready`, `missing`, `unhealthy` or `error`.
- Probe details expose migration count/version but never paths or provider-specific connection information.
- SQLite initialization runs as a hosted service.
- Initialization failure does not stop the HTTP process; liveness remains true and readiness remains false.

## Diagnostics safety

API responses must not expose:

- workspace or database paths;
- connection strings;
- environment variables;
- passwords, secrets or tokens;
- exception messages or stack traces.

The initializer logs only the exception type when startup storage initialization fails.

## Governance correction

Outbox functionality merged in PR #15 was implemented ahead of its canonical place and is now registered in `docs/execution/EARLY_CAPABILITIES.md` for future VS-040 certification. It remains compiled and tested but does not contribute completion credit to VS-014 or VS-040.

The canonical audit, RetroSpec and RED evidence paths for VS-014 now refer exclusively to API and health.

## Operational evidence

CI starts real Kestrel hosts on ephemeral loopback ports and proves both healthy and unready dependency journeys without mocks or fixed ports. Evidence contract: `dotnet.api-health-integration`.

## Follow-on constraints

- VS-015 Control Center shell must consume the versioned API instead of bypassing it.
- Any remote exposure must add authentication, authorization, TLS and deployment policy before enabling remote binding.
- New readiness dependencies must implement `IReadinessProbe` and return sanitized stable statuses.
- Diagnostics expansion must be explicitly reviewed for information disclosure.
- Liveness must never depend on external providers, models or long-running jobs.

## Next slice

`VS-015 — Control Center shell`.
