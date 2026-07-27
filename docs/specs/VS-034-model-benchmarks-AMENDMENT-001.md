# VS-034 — Spec Amendment 001: primary candidates and fallback separation

## Finding

The initial fallback section defined ordered `fallbackModelIds[]` but did not define which models participate in normal ranking. If all catalog models were ranked, an eligible fallback model would already be selected normally and fallback mode would be unreachable.

## Binding correction

Each role policy additionally contains:

```text
primaryModelIds[]
```

Rules:

- normal eligibility/ranking considers only exact IDs listed in `primaryModelIds`;
- fallback evaluation considers only `fallbackModelIds` and preserves their declared order;
- the two lists must be non-empty where the corresponding mode is expected;
- duplicate IDs inside a list are invalid;
- an ID may not appear in both lists;
- every listed ID must exist in the same catalog version;
- provider availability may remove listed candidates but cannot add others;
- hard constraints, evidence freshness and confidence apply identically in both modes;
- fallback evaluation begins only when no primary candidate is eligible;
- fallback weighted score is still calculated for evidence/audit but does not reorder the declared fallback chain.

## Revised selection flow

```text
validate exact role policy
→ evaluate primaryModelIds
→ rank eligible primary candidates
→ select best ranked candidate when any exists
→ otherwise evaluate fallbackModelIds in declared order
→ select first fully eligible fallback
→ otherwise fail closed
```

## Compatibility

This amendment is additive to the initial specification and is binding for the governance contract, implementation, journey and RetroSpec. No production code existed when the correction was made.
