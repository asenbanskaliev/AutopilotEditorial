# VS-128 — Bundled OpenCode and strong live MCP audit

## User outcome

A pinned real OpenCode CLI authenticates to OpenCode Zen without persisting the API key and completes a bounded editorial journey through the real AutopilotEditorial Authoring and Production MCP servers.

## Scope

- Install `opencode-ai` version `1.15.5` into an isolated `.runtime/vs128` prefix.
- Publish `BookStudio.Mcp.Authoring` and `BookStudio.Mcp.Production` into the disposable runtime.
- Supply the repository secret only through `OPENCODE_AUTH_CONTENT`.
- Register both local stdio MCP servers in a temporary `opencode.json`.
- Query the pinned version, authenticated provider, live model catalogue and MCP status.
- Select only an allowlisted free Zen model.
- Execute six independently timed and observable stages:
  1. create and register a Spanish briefing;
  2. create and register a structured outline;
  3. write and register a 400–650 word chapter;
  4. validate the chapter;
  5. prepare a release and run production preflight;
  6. start fresh OpenCode processes and resume validation/preflight without duplicate writes.
- Print `STAGE n/6 START`, `PASS` or a precise failure.
- Scan captured output and evidence candidates for full and partial secret leakage.
- Persist only sanitized hashes, durations and booleans.

## Out of scope

- A complete commercial-length book.
- Paid models.
- Provider billing reconciliation.
- Installing OpenCode globally on a developer machine.
- Claiming final literary quality from one bounded chapter.

## Acceptance scenarios

### Scenario: strong staged journey passes

Given `OPENCODE_ZEN_API_KEY` exists as a repository secret
When the live workflow executes on the pull-request head
Then the pinned CLI is installed in an isolated prefix
And OpenCode recognizes the ephemeral Zen credential
And an approved free model is available
And both MCP servers are connected
And briefing, outline and chapter artifacts are registered through MCP tools
And the chapter validation executes
And release preparation and preflight execute
And fresh OpenCode processes rediscover both MCP servers
And resume performs only read-only validation and preflight
And no duplicate registration or release preparation is requested
And sanitized evidence is uploaded.

### Scenario: one stage stalls or fails

Given a provider or MCP call does not complete
When its strict stage timeout expires
Then the workflow fails on that named stage
And it does not wait for the former monolithic twelve-minute timeout
And no false PASS evidence is written.

### Scenario: credential is absent

Given the repository secret is unavailable
When the workflow starts
Then it fails before live provider execution
And no degraded or skipped result is reported as PASS.
