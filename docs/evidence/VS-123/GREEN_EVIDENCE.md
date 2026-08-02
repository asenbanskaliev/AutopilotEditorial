# VS-123 GREEN_EVIDENCE

Status: IMPLEMENTED — pending final same-head validation.

## GREEN-I
- Typed provider, quote, request and result contracts.
- Provider registry with format routing and fail-closed validation.
- Deterministic reference provider emits real EPUB, PDF, DOCX and KDP package bytes.
- Atomic workspace-confined writes, exact SHA-256, media type, byte size and provenance.
- Hard quote/result cost ceiling and currency consistency.
- Idempotent replay preserves exact bytes.

## GREEN-E
`PublicationArtifactPipelineIntegrationSmoke` is wired into the existing integration executable and opens generated containers, validates required entries and the PDF signature, verifies evidence, replays deterministically and rejects invalid budget policy.

Final PASS requires Plan Integrity, Governance Gates and .NET CI green on one final SHA.
