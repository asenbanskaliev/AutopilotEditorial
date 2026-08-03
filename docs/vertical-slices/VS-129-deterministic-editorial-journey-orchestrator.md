# VS-129 — Deterministic Editorial Journey Orchestrator

## Outcome

A natural-language book idea advances through briefing, outline, chapter, validation, independent review, release preparation and preflight without trusting textual model completion claims.

## Invariants

- Draft IDs are generated as `{projectId}.draft.{slug}`.
- One checkpoint owns one request fingerprint.
- Resume does not repeat verified generation, registration or release preparation.
- Every write is followed by read-back verification of ID, version, hash and length.
- Generated content is bounded and rejected when empty, wrapped, malformed or unsafe.
- Independent review must return `Pass` before release.
- Preflight must pass with no blocking reasons.
- PASS and FAIL evidence contains stable codes, not manuscript text or secrets.

## Acceptance

A fresh journey generates and persists briefing, outline and chapter once, validates the chapter, obtains independent approval, prepares one release, passes preflight and marks the checkpoint complete.

A resumed journey verifies the request fingerprint and persisted state and does not duplicate completed writes.

A fingerprint conflict, postcondition mismatch, non-passing review or blocked preflight stops the journey and persists a sanitized failure event.
