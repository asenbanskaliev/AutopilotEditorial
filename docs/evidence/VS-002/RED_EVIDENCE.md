# VS-002 — Dual Red Evidence

## RED-I

GitHub Actions workflow `Governance Gates`, run `30209167574`, job `89812378690`, failed in the governance test step after both plan-integrity and completion-policy checks passed.

The failure is expected because the CI provider catalog, evidence schema, validator and local evidence runner do not yet exist.

## RED-E

The repository cannot currently execute an approved local fallback and produce a normalized evidence envelope. Therefore a provider outage or unavailable hosted minutes cannot be represented as PASS, FAIL or BLOCKED through a common contract.

## Confirmation

- The environment and plan checks are healthy.
- The failure is caused by missing VS-002 behavior.
- No check was skipped or relabeled as PASS.
