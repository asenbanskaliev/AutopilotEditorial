# VS-123 — Provider-Backed Publication Artifact Pipeline

## Intent
Replace simulated artifact production with a real provider boundary and a deterministic reference implementation that emits valid EPUB, PDF, DOCX and KDP package containers.

## Invariants
- One selected provider owns one production request.
- Required formats must be supported before execution.
- Quote and result currency must match policy.
- Quoted and charged cost must not exceed the configured ceiling.
- Outputs remain confined to the workspace.
- Writes are atomic and restart replay is byte-idempotent.
- Every artifact includes media type, byte size, SHA-256 and provider/proof provenance.
- Missing, incomplete or unverified formats fail closed.

## Acceptance
The executable integration smoke creates and opens EPUB, DOCX and KDP ZIP containers, verifies the PDF header, checks exact evidence, repeats the request without byte drift and rejects invalid budgets.
