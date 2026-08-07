# Execution Status

- Program version: 0.12.0
- Current phase: F3-OPENCODE
- Current slice: VS-035 — Context compiler
- State: READY
- Open PR: none
- Blocking gate: none
- Next action: implement context manifest, deterministic budget allocation, trust labels, provenance binding and integration evidence
- Full program gate: NOT_READY

## MCP Release Package (v0.12.0)

The 5 MCP servers are packaged and ready for distribution.

- Release ZIP: `.runtime/release/BookStudio-mcp-0.12.0.zip`
- SHA-256: `69B527E46225EF4A0F950E0C84A26065DFBA7DDBC542A3E8A77D5B37919A193A`
- Active tools: 10 (2 per server)
- Tool reference: `docs/MCP-TOOLS.md`
- Publish script: `scripts/Publish-McpServers.ps1`
- Package script: `scripts/New-ReleasePackage.ps1`
- SDK fixed: `10.0.301` (was `10.0.204` — not installed)
- opencode.json: uses `dotnet` from PATH (was hardcoded to non-existent SDK path)
- Installer: `Install-BookStudio.ps1` now materializes `opencode.json` with real install paths (replaces `{{INSTALL_ROOT}}` placeholder)
- Package layout: `servers/<name>/BookStudio.Mcp*.dll` (fixed template that wrongly assumed `servers/publish/`)

## Functional host (proof of end-to-end book journey)

A runnable end-to-end editorial journey host is implemented in `src/BookStudio.Worker`
and composes the proven journey with real OpenCode model invocation and the published
MCP servers (authoring, quality, production). It accepts a natural-language idea and
produces briefing → outline → chapter → validation → independent review → release →
preflight, ending in a `Complete` PASS.

- Command: `dotnet run --project src/BookStudio.Worker -- "<idea>" [--title "..." ] [--project id] [--language es-ES]`
- CLI added: `CliOptions` parsing in `Program.cs`.
- Evidence (live runs):
  - `book-live-01` — `src...`/autopilot chapter (spanish literary prose), PASS all stages.
  - `bookstudio-worker/libro-live-02.journey-summary.json` — PASS all 8 stages, review `Pass`.
- Reviewer/writer rely on a real editorial-capable model; models used: `github-copilot/claude-sonnet-4.6`.

## Full-book production (N chapters)

`create-full-book` produces a complete multi-chapter book with real planning, per-chapter
generation, immutable persistence, checkpoint resume and rolling summaries.

- Command: `dotnet run --project src/BookStudio.Worker -- create-full-book "<idea>" --title "..." --project id --chapters N --words-per-chapter N [--context-budget N] [--model-summary]`
- New production adapters in `src/BookStudio.Autopilot/EditorialJourney/FullBookProductionAdapters.cs`:
  - `GatewayFullBookChapterRepository` — persists chapters via the artifact gateway + SQLite metadata (`full_book_chapter`), so `IFullBookChapterRepository` is no longer test-only.
  - `DeterministicFullBookSummarizer` and `OpenCodeFullBookSummarizer` (model rolling summary, `--model-summary`).
- Resilience: `CommercialManuscriptPolicy.ExtractChapter` isolates the real chapter heading from noisy OpenCode stdout before strict validation (word window + heading policy still enforced).
- Evidence (live runs, `.runtime/bookstudio-worker/`):
  - `fullbook-live-01.full-book-summary.json` — 2 chapters deterministic summaries, PASS, chapter shas `f580bfa9...`/`46bcad03...` v1.
  - `fullbook-live-02.full-book-summary.json` — 2 chapters model summaries + rolling summary, PASS, resume-idempotent.
- Tests: VS-132 full-book orchestration, VS-138 commercial adapters remain green.

Validation is self-contained: each run leaves an immutable artifact store under
`.runtime/<project>/workspace/.bookstudio/artifacts` and a reconciliation summary JSON.