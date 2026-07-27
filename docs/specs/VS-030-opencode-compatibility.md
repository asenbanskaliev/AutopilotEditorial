# VS-030 — OpenCode compatibility

## Status

`SPECIFICATION`

## Objective

Create a provider-neutral Application port and a bounded HTTP adapter that can determine whether an OpenCode server is reachable and exposes the exact API features required by the next OpenCode slices, without creating sessions, sending prompts, invoking models or mutating remote configuration.

## Official compatibility baseline

Protocol surface expected from the current OpenCode headless server:

- `GET /global/health` returns health and server version;
- server documentation exposes an OpenAPI 3.1 contract;
- provider, agent and MCP discovery are read-only;
- session create/read/status, asynchronous prompt and abort are required for VS-031;
- event and global-event SSE endpoints are required for VS-032.

The adapter must not assume that a healthy server is compatible. Health, version and features are separate facts.

## Application contract

Application owns:

- `IOpenCodeCompatibilityProbe`;
- immutable health and compatibility results;
- stable feature identifiers;
- compatibility state and safe failure codes.

Application must not reference HTTP, JSON DOM, credentials, URLs or OpenCode SDK types.

## Adapter configuration

`OpenCodeEndpointOptions` contains:

- absolute base URI;
- optional Basic-auth username/password;
- request timeout;
- maximum health response bytes;
- maximum specification response bytes.

Validation rules:

- scheme is `http` or `https` only;
- URI has no user-info, query or fragment;
- base path is `/` only;
- HTTP is allowed only for loopback hosts;
- username/password are bounded and free of control characters;
- password cannot exist without username;
- timeout and byte limits are positive and bounded;
- secrets never appear in result, exception, logs or evidence.

## Compatibility journey

```text
validate endpoint options
→ GET /global/health
→ enforce timeout/status/content-type/byte bound
→ parse exact healthy/version fields
→ GET /doc with OpenAPI-oriented Accept headers
→ enforce timeout/status/content-type/byte bound
→ inspect OpenAPI 3.1 paths without executing them
→ detect stable feature matrix
→ calculate required missing features
→ return compatible/degraded/unavailable/authentication-required report
```

## Required features

Stable IDs:

- `health` — `GET /global/health`;
- `providers.list` — `GET /provider`;
- `agents.list` — `GET /agent`;
- `mcp.status` — `GET /mcp`;
- `sessions.list` — `GET /session`;
- `sessions.create` — `POST /session`;
- `sessions.get` — `GET /session/{id}`;
- `sessions.status` — `GET /session/status`;
- `sessions.prompt_async` — `POST /session/{id}/prompt_async`;
- `sessions.abort` — `POST /session/{id}/abort`;
- `events.project` — `GET /event`;
- `events.global` — `GET /global/event`.

Every feature is detected from the OpenAPI document, never by invoking mutating endpoints.

## OpenAPI inspector

Accepted document:

- JSON object;
- `openapi` string beginning with `3.`;
- `paths` object;
- bounded depth and total bytes;
- exact path-template and lowercase operation lookup;
- duplicate/ambiguous JSON properties rejected by strict parsing policy where observable.

HTML documentation is not interpreted as executable markup. If `/doc` does not return a JSON OpenAPI document, the report is degraded with safe code `openapi_document_unavailable`.

## Result states

- `compatible`: health is true and all required features are present;
- `degraded`: server is healthy but specification is missing/invalid or required features are absent;
- `unhealthy`: health endpoint responds with `healthy=false`;
- `authentication_required`: HTTP 401 or 403;
- `unavailable`: timeout, connection, invalid status or invalid health payload.

The report includes:

- state;
- safe code;
- sanitized server version when available;
- sorted detected features;
- sorted missing required features;
- bounded evidence facts without endpoint credentials or response bodies.

The `healthy` evidence fact is tri-state: `true` only after a valid `healthy=true` response, `false` only after a valid `healthy=false` response, and `unknown` when authentication, transport, status, bounds or payload validation prevents health determination.

## Security and operational requirements

- no automatic retry in this slice;
- cancellation propagates;
- redirects are not followed by the owned client handler;
- at most two HTTP requests per probe;
- only GET is sent;
- Basic Authorization is emitted only when configured;
- response streams are bounded while reading, not after buffering;
- JSON max depth is bounded;
- malformed payloads map to stable safe codes;
- all error messages are path-, credential- and body-free;
- HttpClient lifetime is externally owned or disposed by an explicit factory owner.

## TDD Dual

### RED-I

Static contracts require missing Application port/models, endpoint options, HTTP adapter, OpenAPI inspector, integration project and CI registration.

### RED-E

A local contractual HTTP server must prove:

- compatible server;
- healthy but missing feature;
- unhealthy;
- unauthorized and authorized Basic auth;
- malformed health;
- malformed/unsupported OpenAPI;
- response too large;
- timeout and cancellation;
- no POST/PUT/PATCH/DELETE requests;
- exact request count;
- no secret leakage.

## Gates

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ OPENCODE_HEALTH_PASS
∧ FEATURE_DETECTION_PASS
∧ AUTH_AND_BOUNDARIES_PASS
∧ NO_SIDE_EFFECT_DISCOVERY_PASS
∧ NO_ORPHANS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

## Explicit exclusions

- session creation or deletion;
- prompts, models or provider execution;
- SSE connection/reconciliation;
- dynamic MCP configuration;
- OpenCode process launch or upgrade;
- credential persistence;
- model/provider benchmarking.

Those belong to later slices.
