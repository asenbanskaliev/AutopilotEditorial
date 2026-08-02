# VS-128 — Bundled OpenCode and live MCP audit

## User outcome

A repository change proves that a pinned real OpenCode CLI can authenticate to OpenCode Zen without persisting the API key, discover the AutopilotEditorial MCP, invoke one real MCP tool through an approved free model, and rediscover the MCP from a fresh process.

## Scope

- Install `opencode-ai` version `1.15.5` into an isolated `.runtime/vs128` prefix.
- Publish `BookStudio.Mcp` into the same disposable runtime.
- Supply the repository secret only through `OPENCODE_AUTH_CONTENT`.
- Register the local stdio MCP in a temporary project `opencode.json`.
- Query the installed version, authenticated provider, live model catalogue and MCP status.
- Select only an allowlisted free Zen model.
- Ask the model to invoke `book.artifact.get` exactly once with deterministic non-sensitive identifiers.
- Start a new OpenCode process and verify MCP rediscovery.
- Scan all captured output and evidence candidates for full and partial secret leakage.
- Persist only sanitized hashes, durations and booleans.

## Out of scope

- Long-form book generation.
- Paid models.
- Provider billing reconciliation.
- Installing OpenCode globally on a developer machine.
- Claiming literary quality from this smoke audit.

## Acceptance scenarios

### Scenario: real CLI and MCP path passes

Given `OPENCODE_ZEN_API_KEY` exists as a repository secret
When the live workflow executes on the pull-request head
Then the pinned CLI is installed in an isolated prefix
And OpenCode recognizes the ephemeral Zen credential
And an approved free model is available
And `autopilot_editorial` is connected
And the live prompt exercises the MCP tool path
And a fresh OpenCode process rediscovers the MCP
And sanitized evidence is uploaded.

### Scenario: secret is missing

Given the repository secret is unavailable
When the live workflow starts
Then the workflow fails before model execution
And it never reports a skipped or false-positive PASS.

### Scenario: secret leakage

Given any captured output contains the full key or a stable key fragment
When evidence is prepared
Then the audit fails
And no evidence artifact is published as PASS.

### Scenario: free model unavailable

Given no model in the approved free allowlist appears in `opencode models opencode`
When the audit selects a model
Then it fails closed rather than using a paid model.

## Evidence

The workflow uploads `artifacts/vs128/opencode-live-mcp-audit.json`. It contains no prompt transcript, API key, auth file or manuscript content.
