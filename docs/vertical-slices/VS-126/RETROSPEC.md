# RetroSpec — VS-126

## What changed
Installation moved from repository-only instructions to a signed, digest-bound, resumable and guided Windows flow that installs and launches the real Control Center without technical commands.

## What was learned
Installation readiness is not equivalent to producing a build artifact. The product needs an authority that verifies provenance, protects secrets, discloses cost limits, survives interruption, proves the installed runtime is healthy and emits exact diagnostics when it is not.

The final CI defect was not an application startup failure. The test launched the installed executable with `--urls`, while the composition root reapplied `ControlCenter:Url`; the smoke probe then queried a different port. Binding the installed process and probe to the same `ControlCenter__Url` removed the false negative. Capturing PID, command, URL, exit code, stdout and stderr prevents this class of ambiguity from recurring.

## Validated outcome
Exact implementation head `d4b2e168040972b209b722444803a99f48e14e58` passed:
- Plan Integrity run `30763347809`;
- Governance Gates run `30763347810`;
- .NET CI Windows installer E2E run `30763347829`.

## Follow-up
Add the protected release workflow that builds and signs the distributable with the production certificate, execute the installer across supported Windows versions, and define service-account credential handling. These are follow-up hardening tasks, not claims of VS-126.
