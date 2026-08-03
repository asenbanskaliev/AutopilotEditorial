# VS-130 — Production Editorial Journey Adapters & Live Autonomous Proof

## Goal

Connect the deterministic VS-129 orchestrator to production-capable OpenCode Zen generation, an independent reviewer, durable SQLite state, real MCP stdio processes, observable progress and restart-safe receipts.

## Product contracts

- `OpenCodeEditorialModelInvoker` executes only approved free models, falls back deterministically and caches by purpose + prompt hash + context hash + candidate set.
- `OpenCodeEditorialContentGenerator` generates briefing, outline and chapter while preserving provider/model/prompt metadata.
- `OpenCodeIndependentEditorialReviewer` uses a separate reviewer candidate policy and requires `DECISION: PASS|REVISE|BLOCKED`.
- `SqliteEditorialJourneyCheckpointStore` is the durable checkpoint authority and writes execution receipts transactionally.
- `StdioMcpEditorialArtifactGateway` invokes real Authoring, Quality and Production MCP servers through JSON-RPC stdio and persists postcondition receipts for restart-safe `GetAsync`.
- `JsonLineEditorialJourneyProgressSink` emits machine-readable stage progress without manuscript or credential content.
- `NaturalLanguageEditorialJourneyService` accepts one natural-language idea, derives a title when absent and enriches sparse ideas with conservative assumptions.

## Fail-closed rules

- No approved model candidate: fail.
- All writer or reviewer candidates fail: fail.
- Empty or malformed reviewer decision: fail.
- MCP JSON-RPC error or `isError=true`: fail.
- MCP response does not reference the expected artifact: fail.
- Quality or production preflight blocks: fail.
- Persisted request fingerprint differs: fail.
- Secrets are never accepted as constructor arguments, serialized into checkpoints or emitted by progress events.

## Acceptance proof

1. Compile the complete solution and the VS-130 harness.
2. Prove SQLite checkpoint restart and receipt recovery.
3. Prove writer/reviewer separation and metadata retention.
4. Run the pinned OpenCode Zen live generation and real Authoring/Production MCP journey.
5. Verify restart and read-only resume without duplicate registration or release preparation.
6. Scan live evidence for secret leakage and reject persisted `auth.json`.
7. Require exact-head green Plan Integrity, Governance, .NET CI, Windows installer E2E and VS-130 production journey checks before merge.
