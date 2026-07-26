# VS-010 ChangeRequest 001 — Baseline CI provider

## Trigger

The `.NET CI` job created for commit `72b4d3b68b1e1f3b27358229dd4352e640e2a5f1` remained queued because no self-hosted runner claimed it.

## Original expectation

The VS-010 external GREEN expected the self-hosted provider to restore, build and run architecture fitness.

## Change

Use `github-hosted-default` for the lightweight solution baseline and install the exact SDK from `global.json` with `actions/setup-dotnet@v5`.

Retain `github-self-hosted-default` as the preferred provider for intensive .NET, integration and rendering contracts.

## Justification

- VS-002 explicitly permits provider fallback when the validation contract is equivalent.
- The baseline has no external packages and is suitable for a hosted runner.
- Waiting indefinitely for a runner would violate durable progress and provider abstraction.
- The validation contract, commands and evidence remain the same.

## Impact

- BehaviorSpec GREEN-E changes from “self-hosted .NET CI” to “approved .NET-capable provider”.
- No production code or architecture policy changes.
- A dedicated self-hosted certification remains required in operations/certification slices.

## Approval

**APPROVED** — equivalent provider, identical source SHA and validation contract.
