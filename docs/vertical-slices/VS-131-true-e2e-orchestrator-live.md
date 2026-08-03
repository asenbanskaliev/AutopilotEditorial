# VS-131 — True End-to-End Orchestrator Live Proof

## Goal

Prove that the actual production C# composition executes the complete editorial journey. The Python audit from VS-128/130 is no longer the orchestration source of truth.

## Production path

Natural-language idea → `NaturalLanguageEditorialJourneyService` → `DeterministicEditorialJourneyOrchestrator` → `OpenCodeEditorialContentGenerator` → real Authoring MCP → real Quality MCP → `OpenCodeIndependentEditorialReviewer` → real Production MCP → SQLite checkpoints and receipts → process disposal → new composition → resume.

## Acceptance

- OpenCode is pinned and authenticated through `OPENCODE_AUTH_CONTENT` only.
- Writer and reviewer execute through separate model purposes and candidate policies.
- Authoring, Quality and Production MCP processes are published from repository source and invoked through JSON-RPC stdio.
- Three draft receipts and one release receipt are persisted exactly once.
- A second complete composition loads the durable checkpoint and returns `Resumed=true` without adding writes.
- Quality MCP absence or blocking output fails closed.
- Evidence contains no manuscript text or credentials.
- `auth.json` is not created.
- All checks pass on the exact PR head before merge.
