# RetroSpec — VS-126

## What changed
Installation moved from repository-only instructions to a signed, digest-bound, resumable and guided Windows flow.

## What was learned
Installation readiness is not equivalent to a build artifact. The product needs an authority that verifies provenance, protects secrets, discloses cost limits, survives interruption and produces exact evidence.

## Follow-up after validation
Add the release workflow that builds and Authenticode-signs the distributable, then execute the installer matrix on supported Windows versions. These are not claimed by this slice until validated.
