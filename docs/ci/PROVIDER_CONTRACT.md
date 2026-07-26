# CI Provider Contract

## Objective

Run validation contracts through interchangeable providers while preserving equivalent evidence and honest outcomes.

## Providers

- `github-hosted-default`: lightweight and governance checks.
- `github-self-hosted-default`: .NET, integration and render workloads.
- `circleci-default`: optional external provider; disabled until credentials and project setup are confirmed.
- `local-evidence-default`: controlled fallback that writes a hashed evidence envelope.

## Results

Only these results are valid:

- `PASS`: the validation executed and succeeded.
- `FAIL`: the validation executed and failed.
- `BLOCKED`: the validation could not execute or no approved equivalent existed.

`SKIPPED` is never treated as `PASS`.

## Selection

1. Load the provider catalog.
2. Filter enabled providers by capability.
3. Evaluate runtime availability.
4. Select the lowest unique priority value.
5. Execute the exact contract or an explicitly approved equivalent.
6. Normalize evidence.
7. Preserve retry and fallback history.

## Local evidence

Local evidence is allowed only when `localEquivalentAllowed` is true for the validation contract.

The runner:

- executes an argument array with `shell=False`;
- captures exit code, stdout and stderr;
- records source SHA and timestamps;
- hashes stdout and stderr;
- writes no environment secrets;
- returns the original failure code;
- returns `2` for BLOCKED.

## Secrets

The repository contains only secret reference names. Values are provided through the execution environment or an operating-system secret store.

## Provider outage

A provider outage triggers fallback only when the alternative can satisfy the same capability and evidence policy. Otherwise the gate remains BLOCKED.
