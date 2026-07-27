# VS-031 — TestChangeRequest TCR-031-001

## Trigger

The real solution build, architecture fitness and session lifecycle journey passed, but two static assertions targeted formatting or the wrong responsibility:

1. the adapter contract searched for the isolated literals `"prompt_async"` and `"abort"`, while the implementation uses the exact path suffixes `"/prompt_async"` and `"/abort"`;
2. the contract searched for `Authorization` in the generic socket server, while authentication policy and assertions live in `OpenCodeSessionLifecycleJourney` and the server intentionally records headers generically.

## Approved test change

- assert the exact path suffixes `"/prompt_async"` and `"/abort"` in the adapter;
- keep `TcpListener`, `Content-Length` and request recording assertions in the server;
- assert `Authorization` in the journey where Basic-auth behavior and no-leak requirements are exercised.

## Preserved requirements

- only the five planned session lifecycle paths remain permitted;
- async prompt and abort retain exact POST path assertions;
- every request header remains captured by the contractual server;
- the journey still requires Basic Authorization on health, OpenAPI and session mutation requests;
- credentials must not appear in results or errors;
- no functional scenario, method/path inventory, bound, idempotency assertion or cancellation check is removed.

## Test Auditor decision

**APPROVED** — the static checks are moved to the precise code ownership locations while the already-green real HTTP journey remains unchanged and mandatory.
