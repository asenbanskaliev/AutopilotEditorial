# Architecture Test Instructions

## Allowed

- Read repository contracts, project XML and compiled PE metadata.
- Fail with precise project, reference and policy evidence.
- Validate architecture without loading or executing product entry points.
- Remain dependency-free where the SDK provides sufficient APIs.

## Forbidden

- Product behavior or domain helpers.
- Mutating repository files during a test.
- Network, database or provider calls.
- Duplicating the canonical architecture graph in test code.
- Converting missing build outputs into warnings.

The canonical rules come only from `docs/architecture/architecture-policy.json`.
