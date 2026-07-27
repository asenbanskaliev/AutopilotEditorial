# VS-031 — RED Evidence

## Scope

Session lifecycle is intentionally absent before implementation.

Required behavior:

```text
compatibility gate
→ create session
→ get session
→ enqueue bounded text prompt asynchronously
→ normalize status map
→ abort explicitly
→ enforce local idempotency
```

## RED-I — Missing implementation contracts

The initial branch does not contain:

- `IOpenCodeSessionLifecycle`;
- provider-neutral session commands/results/errors;
- shared session input validation;
- HTTP session lifecycle client;
- bounded local idempotency ledger;
- session lifecycle integration project;
- contractual HTTP server and journey;
- architecture and CI registration.

Governance must fail until these components exist and are linked.

## RED-E — Missing observable journey

No existing executable proves:

- compatibility failure blocks all mutation;
- create/get mapping;
- async prompt exact HTTP 204;
- status normalization for idle, busy, retry and unknown;
- explicit abort true/false;
- same-key same-command replay;
- same-key different-command conflict;
- concurrent duplicate collapse;
- failed reservation release and later retry;
- Basic auth without secret leakage;
- bounds and malformed provider responses;
- timeout/cancellation;
- absence of delete, patch, shell, command, share and file endpoints.

## Expected initial gates

```text
Plan Integrity = PASS
Governance Gates = FAIL
.NET CI may remain PASS because no incomplete implementation is registered yet
```

The Governance failure is the required RED-I signal. No production code may be accepted as GREEN until the real HTTP journey is registered and passes.

## Test independence

- The Governance contract is static and checks architecture, files and permanent wiring.
- The external journey must use a real loopback HTTP server, not a mocked `IOpenCodeSessionLifecycle`.
- Compatibility must be exercised through the real VS-030 probe or a contract-faithful real HTTP surface, never by assuming capabilities.
- Tests may not call private adapter methods or provider routers directly.

## Repair policy

Any expectation change after RED requires a documented `TestChangeRequest` proving that no observable requirement was weakened.
