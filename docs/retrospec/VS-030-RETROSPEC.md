# VS-030 — RetroSpec

## Implemented contract

BookStudio now has a provider-neutral compatibility boundary for an external OpenCode HTTP server.

```text
BookStudio.Application.OpenCode
→ BookStudio.OpenCode
→ bounded read-only HTTP discovery
```

The slice detects whether a server is reachable, healthy and structurally compatible without creating sessions, sending prompts, executing models or changing remote configuration.

## Application boundary

Application owns:

- `IOpenCodeCompatibilityProbe`;
- `OpenCodeCompatibilityReport`;
- `OpenCodeCompatibilityStates`;
- `OpenCodeFeatureIds`.

The Application contract contains no HTTP client, URI, JSON DOM, credentials or OpenCode SDK types.

## Stable states

```text
compatible
degraded
unhealthy
authentication_required
unavailable
```

Meaning:

- `compatible`: valid `healthy=true` and all required features declared;
- `degraded`: valid healthy server but OpenAPI is missing/invalid or features are absent;
- `unhealthy`: valid `healthy=false`;
- `authentication_required`: HTTP 401/403;
- `unavailable`: connection, timeout, status, bounds or health-payload failure.

## Evidence facts

The report contains a sorted, credential-free facts dictionary.

```text
requests = 1|2
healthy  = true|false|unknown
openapi  = <sanitized 3.x version when available>
```

`healthy` is evidence-based:

- `true`: a valid health payload explicitly returned true;
- `false`: a valid health payload explicitly returned false;
- `unknown`: health could not be established.

A state other than `unhealthy` never implies `healthy=true` by itself.

## Required feature matrix

```text
health
providers.list
agents.list
mcp.status
sessions.list
sessions.create
sessions.get
sessions.status
sessions.prompt_async
sessions.abort
events.project
events.global
```

The eleven non-health features are discovered from OpenAPI paths/operations only.

## Endpoint options

`OpenCodeEndpointOptions` requires:

- absolute base URI;
- HTTP loopback or HTTPS;
- root path `/`;
- no user-info, query or fragment;
- optional bounded Basic username/password;
- bounded request timeout;
- bounded health bytes;
- bounded OpenAPI bytes.

Password without username is invalid. Control characters and unsafe URL components are rejected.

## Probe journey

```text
validate options
→ GET /global/health
→ bounded status/content-type/body validation
→ parse exact healthy/version
→ GET /doc only when health is valid and true
→ bounded OpenAPI validation
→ normalize path parameters
→ inspect GET/POST declarations
→ calculate detected/missing features
→ return deterministic report
```

Maximum requests per probe: `2`.

Allowed outbound method: `GET` only.

## Health contract

Expected JSON fields:

```json
{
  "healthy": true,
  "version": "1.2.3"
}
```

Rules:

- root must be an object;
- `healthy` must be boolean;
- `version` must be a bounded non-empty string without control characters;
- JSON depth is limited;
- malformed or oversized health maps to safe unavailable codes.

## OpenAPI contract

Accepted document:

- JSON object;
- `openapi` string beginning with `3.`;
- `paths` object;
- bounded total bytes;
- maximum JSON depth 64;
- exact lowercase HTTP operation lookup;
- duplicate properties rejected where inspected.

Path parameters such as `{sessionID}` or `:sessionID` normalize to `{id}` for matching.

HTML or another non-JSON response is not executed or interpreted. It produces `openapi_document_unavailable`.

## HTTP and cancellation behavior

The owned probe client:

- disables automatic redirects;
- uses `ResponseHeadersRead`;
- applies connect/request timeout;
- checks Content-Length when available;
- also enforces bytes while streaming;
- propagates caller cancellation;
- maps internal timeout to `request_timeout`;
- maps connection/I/O failures to `connection_failed`;
- performs no automatic retry.

An externally supplied `HttpClient` remains externally owned. The factory-created client is disposed by the probe.

## Authentication

When both username and password are configured, each request includes Basic Authorization.

Reports, exceptions and normalized evidence never include:

- username;
- password;
- Authorization value;
- endpoint URL;
- response body.

A 401/403 before valid health returns:

```text
state=authentication_required
healthy=unknown
```

A 401/403 on `/doc` after valid health preserves:

```text
state=authentication_required
healthy=true
```

## Integration journey

Project:

```text
tests/BookStudio.Tests.OpenCodeCompatibility
```

It hosts a contractual HTTP server over real loopback sockets and verifies 13 scenarios, 18 requests and the 12-feature matrix.

Verified result:

```text
OPENCODE_COMPATIBILITY_PASS scenarios=13 requests=18 features=12
```

Exit code is 0 and stderr is empty.

## CI contract

```text
dotnet.opencode-compatibility-integration
```

Normalized evidence:

```text
artifacts/ci/dotnet-opencode-compatibility-integration.json
```

The OpenCode journey runs after MCP security and before evidence upload. Every accumulated journey remains mandatory.

## TestChangeRequests

- `TCR-030-001`: typed scheme tokens replace weak lowercase substring checks.
- `TCR-030-002`: health evidence becomes tri-state and gains cumulative journey assertions.

## Deviations

- Compatibility is based on `/global/health` and `/doc`; alternative discovery endpoints are not probed.
- The adapter validates declared API surface rather than invoking mutating operations.
- SSE connection/reconciliation is not executed in this slice.
- TLS certificate policy and OpenCode process lifecycle remain host/deployment concerns.
- OpenAPI `3.x` is accepted; exact 3.1-only rejection is not enforced to preserve compatible 3.x documents.

## Follow-on constraints

- VS-031 must depend on this report and refuse session execution when required session features are missing.
- VS-032 must require both event features before opening SSE streams.
- New required endpoints must extend the stable feature catalog and journey.
- No later optimization may replace bounded streaming with unbounded buffering.
- Mutating endpoints must never be invoked during compatibility discovery.
- Credentials must remain external to reports and persisted evidence.

## Phase result

`VS-030` starts F3-OPENCODE and provides the compatibility gate required by session lifecycle and SSE slices. The full program remains `NOT_READY`.
