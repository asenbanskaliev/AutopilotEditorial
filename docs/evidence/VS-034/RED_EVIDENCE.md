# VS-034 — RED Evidence

## Result

`EXPECTED_RED`

## Baseline

The branch starts from merged VS-033 commit:

```text
b29c8985ba4518f9c0c5d81d8ec2a6ca563d1a43
```

## RED-I — Governance / integration

The specification requires artifacts that do not yet exist:

- provider-neutral benchmark and role-policy contracts;
- immutable bounded benchmark catalog;
- strict repository JSON schema/loader;
- deterministic eligibility and weighted selector;
- stale/missing/low-confidence evidence guards;
- explicit fallback evaluator;
- assignment fingerprint;
- OpenCode provider mapping boundary;
- architecture and CI registration;
- governance contract for all acceptance gates.

Expected failures:

```text
MODEL_BENCHMARK_CONTRACTS_MISSING
MODEL_BENCHMARK_CATALOG_MISSING
MODEL_ASSIGNMENT_SELECTOR_MISSING
MODEL_ASSIGNMENT_FINGERPRINT_MISSING
MODEL_PROVIDER_MAPPING_MISSING
ARCHITECTURE_REGISTRATION_MISSING
CI_CONTRACT_MISSING
```

## RED-E — Contractual journey

No executable journey currently proves:

1. strict repository catalog load;
2. exact role policy version lookup;
3. hard constraint filtering;
4. missing evidence rejection;
5. stale evidence rejection;
6. confidence threshold enforcement;
7. deterministic weighted scoring;
8. deterministic tie-breaking;
9. explicit fallback ordering;
10. fallback hard-constraint enforcement;
11. provider availability only narrowing candidates;
12. assignment/profile fingerprint validation;
13. fail-closed provider mapping;
14. concurrent/cancelled selection without remote mutation.

Expected marker is absent:

```text
OPENCODE_MODEL_BENCHMARKS_PASS scenarios=14 models=5 roles=5 gate=HARD_CONSTRAINTS mutation=NONE
```

## Transition to GREEN

The slice may leave RED only after the governance contract and the real selector/mapper journey pass, followed by Auditoría M, Meta-Audit and RetroSpec. Hard-coded direct model selection or provider-specific scoring is insufficient.
