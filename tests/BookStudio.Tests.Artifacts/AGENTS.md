# Artifact Integration Test Instructions

## Allowed

- Real filesystem journeys in disposable workspace roots.
- Positive, negative, concurrent, cancellation and tamper scenarios.
- References to Application contracts and Infrastructure implementations.
- Deterministic assertions with non-zero process exit on failure.

## Forbidden

- Product behavior, mocks or fake artifact providers.
- Writes outside the disposable workspace.
- Network calls, credentials or long-lived state.
- Suppressing integrity, security or cleanup failures.

The executable must prove observable end-to-end behavior and remain independently runnable from CI.
