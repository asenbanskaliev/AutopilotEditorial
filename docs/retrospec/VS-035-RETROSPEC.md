# VS-035 RetroSpec — Context compiler

## Delivered

A provider-neutral deterministic context compiler with versioned manifests, trust precedence, hard global/per-trust budgets, required-source fail-closed behavior, SHA-256 integrity and reproducible fingerprints.

## Corrections discovered by dual testing

1. The integration test project required architecture and solution registration.
2. The CI evidence contract had to be added to `config/ci/providers.json`; direct journey success alone was insufficient.
3. Test fixtures were kept structurally valid so failures exercise the intended policy rather than constructor validation.

## Durable rules

- Every new normalized CI evidence step must have a matching provider contract.
- Required context is never partially included.
- Equivalent source permutations must compile to the same ordered manifest and fingerprint.
- No compiler path may mutate remote state.

Status: VERIFIED.
