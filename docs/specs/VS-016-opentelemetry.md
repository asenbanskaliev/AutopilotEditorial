# VS-016 — OpenTelemetry End-to-End

## IntentSpec

### Problem

The application has health and diagnostics but cannot explain request latency, background operations, error rates or runtime pressure across hosts. Ad-hoc logs would not provide a coherent three-signal model and could leak workspace data or credentials.

### Objective

Instrument the local platform end-to-end with OpenTelemetry traces, metrics and logs; expose a bounded sanitized operational snapshot; and support validated OTLP export without requiring an external collector for local operation.

## BehaviorSpec

### Versions and dependencies

- OpenTelemetry packages are centrally pinned to stable `1.17.0`.
- ASP.NET Core and .NET runtime instrumentation are enabled.
- OTLP export is optional and disabled by default.
- No vendor-specific telemetry SDK is introduced.

### Service identity

Resource attributes use stable low-cardinality values:

- service name: `BookStudio.ControlCenter`;
- service version from assembly;
- deployment environment;
- instance ID generated for the process and never derived from machine name, user name or workspace path.

### Custom instrumentation

`BookStudioTelemetry` defines:

- ActivitySource `BookStudio`;
- Meter `BookStudio`;
- operation counter;
- operation duration histogram;
- operation failure counter;
- active-operation up/down counter.

Operation names are validated low-cardinality tokens. No artifact IDs, chapter text, paths or prompts are metric dimensions.

### Traces

- ASP.NET Core requests to known local routes are traced.
- Unknown paths are excluded to avoid capturing arbitrary path data.
- Custom operations may create child spans.
- Snapshot trace attributes use an allowlist and never expose query strings, request bodies, headers or workspace data.

### Metrics

- ASP.NET Core, runtime and BookStudio meters are registered.
- The local exporter records metric names and export timestamps, not high-cardinality points.
- Metrics export may be forced during tests and graceful shutdown.

### Logs

- Microsoft.Extensions.Logging is bridged through OpenTelemetry.
- Formatted messages are not exported to the local snapshot.
- The snapshot stores message templates plus an allowlist of safe structured attributes.
- Keys containing `password`, `secret`, `token`, `authorization`, `cookie`, `path`, `prompt`, `content` or `connection` are removed.
- Exceptions are represented only by type, never message or stack trace.

### Bounded local snapshot

- Independent bounded buffers for traces, metrics and logs.
- Capacity configurable from 16 to 2,048 records; default 256.
- Oldest records are discarded on overflow.
- API returns counts, dropped counts and newest-first sanitized records.
- API allows `limit` from 1 to 100.
- Snapshot endpoint itself is excluded from trace storage to avoid recursive noise.

### OTLP

- Disabled unless `Observability:OtlpEnabled=true`.
- Endpoint must be HTTPS or loopback HTTP.
- Headers/credentials are not accepted through application configuration in this slice.
- Export failure must not affect liveness or request success.

### API

`GET /api/v1/observability?limit=N` returns:

- enabled/export configuration without endpoint value;
- trace, metric and log counts;
- dropped counts;
- bounded sanitized records.

No clear/reset endpoint is exposed in this slice.

## TDD Dual

### RED-I

Packages, contracts, options, exporters, instrumentation, endpoint and CI contract are absent.

### RED-E

No real SDK journey proves traces, metrics, logs, propagation, redaction, bounds, force flush and safe API output.

### GREEN-I

Static contracts, package policy, architecture and build pass.

### GREEN-E

A real Kestrel host emits all three signals through OpenTelemetry, flushes them, exposes sanitized records and proves OTLP validation without a collector.

## Audit M

- M1: three-signal and redaction contract.
- M2: provider-neutral contracts and OpenTelemetry adapter boundaries.
- M3: signal, propagation, limit, overflow and redaction tests.
- M4: bounded memory, safe attributes, validated export and no failure coupling.
- M5: request/operation/log → SDK → exporter → snapshot API journey.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.
