# VS-030 — TestChangeRequest TCR-030-001

## Trigger

The endpoint-options governance contract searched for literal lowercase strings:

```text
http
https
```

The implementation intentionally validates schemes through the typed platform constants:

```text
Uri.UriSchemeHttp
Uri.UriSchemeHttps
```

The full integration journey already proves:

- loopback HTTP is accepted;
- non-loopback HTTP is rejected;
- FTP is rejected;
- credentials, path, query and fragment in the base URL are rejected.

## Approved test change

Replace the two weak substring assertions with exact typed-token assertions for:

- `Uri.UriSchemeHttp`;
- `Uri.UriSchemeHttps`.

Preserve all other endpoint, authentication, timeout, byte-bound, GET-only, OpenAPI and CI expectations.

## Preserved requirements

- only HTTP/HTTPS schemes are supported;
- plain HTTP remains loopback-only;
- no unsafe URL component becomes accepted;
- no functional journey is removed or weakened;
- no product implementation changes are required to obtain Governance GREEN.

## Test Auditor decision

**APPROVED** — the static contract is made more precise and aligned with the typed implementation while all observable requirements remain covered by integration tests.

## TCR-030-002 — Health fact semantic correction

### Trigger

Meta-review found that reports produced before a valid health payload used `healthy=true` because the helper inferred health from every state except `unhealthy`.

### Approved product and test change

- make the `healthy` fact tri-state: `true`, `false` or `unknown`;
- preserve `true` for degraded reports reached after a valid healthy response;
- preserve `false` only for a valid `healthy=false` response;
- use `unknown` for pre-health authentication, transport, timeout, status, bounds and malformed-health failures;
- add cumulative assertions to the real HTTP journey without removing any scenario.

### Test Auditor decision

**APPROVED** — this corrects an observable semantic contradiction and strengthens the evidence contract without changing endpoint discovery, request count, authentication, bounds or feature detection.
