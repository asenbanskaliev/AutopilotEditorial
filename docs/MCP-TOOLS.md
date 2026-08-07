# BookStudio MCP Servers — Public Tool Reference

Version: 0.12.0 · Baseline: MCP 2025-11-25 · Runtime: .NET 10

## Overview

BookStudio exposes five bounded MCP servers. Each server is a standalone stdio
process launched by the host (e.g. OpenCode). All tools are synchronous and
deterministic. No tool modifies durable state except where noted.

```
book-core        → artifact reads and comparisons
book-authoring   → draft registration and validation
book-quality     → quality audits and gate evaluation
book-production  → release manifest and preflight
book-ops         → diagnostics and readiness probes
```

---

## book-core

Immutable artifact reads and deterministic comparisons.

### `book.artifact.get`

Read immutable artifact metadata and optionally inline bounded UTF-8 text
without exposing filesystem paths.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `artifactId` | string | yes | Artifact identifier |
| `version` | integer ≥ 1 | yes | Version to read |
| `includeContent` | boolean | no | Inline text content (default: false) |

**Output** — artifact metadata, optional content.

---

### `book.artifact.compare`

Compare two immutable text-compatible artifact versions with a bounded
deterministic line diff.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `artifactId` | string | yes | Artifact identifier |
| `leftVersion` | integer ≥ 1 | yes | Left version |
| `rightVersion` | integer ≥ 1 | yes | Right version |
| `maxDifferences` | integer 1–100 | no | Max diff lines returned (default: 20) |

**Output** — bounded line diff.

---

## book-authoring

Draft registration and integrity validation.

### `book.draft.register` _(write)_

Publish one bounded UTF-8 draft version into the project-confined immutable
Artifact Store. Each call creates a new immutable version.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `artifactId` | string | yes | Artifact identifier |
| `expectedVersion` | integer ≥ 1 | yes | Next version to create (optimistic concurrency) |
| `mediaType` | enum | yes | `text/markdown` or `text/plain` |
| `content` | string 1–524288 chars | yes | Draft content |

**Output** — confirmed version, artifact hash.

---

### `book.draft.validate`

Integrity-check a stored textual draft and return deterministic metrics and
bounded warnings without modifying it.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `artifactId` | string | yes | Artifact identifier |
| `version` | integer ≥ 1 | yes | Version to validate |
| `maximumLineLength` | integer 40–240 | no | Line length warning threshold (default: 120) |

**Output** — metrics (word count, line count, character count), warnings list.

---

## book-quality

Deterministic quality checks and gate evaluation.

### `book.audit.run`

Integrity-check one immutable draft and return bounded deterministic metrics
and quality checks without modifying it.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `artifactId` | string | yes | Artifact identifier |
| `version` | integer ≥ 1 | yes | Version to audit |
| `minimumWords` | integer 1–50000 | no | Minimum word count (default: 1) |
| `maximumSentenceWords` | integer 10–300 | no | Sentence length warning threshold (default: 60) |

**Output** — audit result with metrics, checks, warnings.

---

### `book.gate.evaluate`

Evaluate the draft-basic profile and return PASS or BLOCKED with stable
blocking reasons without persisting approval state.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `artifactId` | string | yes | Artifact identifier |
| `version` | integer ≥ 1 | yes | Version to evaluate |
| `profile` | enum | no | `draft-basic` (default and only current value) |
| `minimumWords` | integer 1–50000 | no | Minimum word threshold (default: 1) |
| `maximumWarnings` | integer 0–100 | no | Max warnings before BLOCKED (default: 3) |
| `blockOnPlaceholders` | boolean | no | Block if placeholders detected (default: true) |

**Output** — `PASS` or `BLOCKED`, blocking reasons list.

---

## book-production

Release manifest preparation and preflight verification.

### `book.release.prepare` _(write)_

Verify source artifacts and publish one canonical immutable release manifest
without rendering or copying source bytes.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `releaseId` | string | yes | Release identifier |
| `expectedVersion` | integer ≥ 1 | yes | Next version (optimistic concurrency) |
| `title` | string 1–200 chars | yes | Book title |
| `language` | string 2–32 chars | yes | Language tag (e.g. `es-ES`) |
| `sources` | array 1–50 | yes | Source artifacts (role + artifactId + version) |

Source roles: `manuscript`, `cover`, `metadata`, `interior-pdf`, `epub`, `supplemental`.

**Output** — confirmed release manifest version.

---

### `book.preflight.run`

Verify an immutable release manifest and all source artifacts against the
deterministic release-basic profile without modifying them.

**Inputs**
| Field | Type | Required | Description |
|---|---|---|---|
| `projectId` | string | yes | Project identifier |
| `releaseArtifactId` | string | yes | Release manifest artifact identifier |
| `version` | integer ≥ 1 | yes | Version to verify |
| `profile` | enum | no | `release-basic` (default and only current value) |

**Output** — PASS or FAIL with per-check results.

---

## book-ops

Diagnostics and operational readiness.

### `book.ops.status`

Run configured readiness probes and return a sanitized operational status
without initializing, repairing or modifying the workspace.

**Inputs** — none.

**Output** — readiness probe results per component.

---

### `book.ops.diagnostics`

Return sanitized readiness checks, the canonical product capability catalog
and stable recommendations without changing durable state.

**Inputs** — none.

**Output** — capability catalog, readiness checks, recommendations.

---

## Security constraints

- All file access is confined to the project workspace sandbox.
- Symlinks that escape the workspace root are rejected.
- Artifact content is bounded (max 512 KB per draft).
- No tool writes logs to stdout in stdio mode.
- Secrets and credentials are never returned in tool output.
- External content is treated as data, not instructions.

---

## Connecting to OpenCode

### Automatic (Windows installer)

Run the packaged installer:

```powershell
.\Install-BookStudio.ps1 -PackagePath .\BookStudio-mcp-0.12.0.zip -ExpectedSha256 <hash>
```

The installer extracts the servers and materializes an `opencode.json` with the
real install paths. Copy that generated `opencode.json` into any project folder
where you want the servers enabled:

```powershell
Copy-Item "$env:LOCALAPPDATA\BookStudio\opencode.json" <your-project-folder>\opencode.json
```

### Manual

Add to `opencode.json` (adjust paths to your install location):

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "book-core":       { "type": "local", "command": ["dotnet", "/opt/bookstudio/servers/book-core/BookStudio.Mcp.dll"] },
    "book-authoring":  { "type": "local", "command": ["dotnet", "/opt/bookstudio/servers/authoring/BookStudio.Mcp.Authoring.dll"] },
    "book-quality":    { "type": "local", "command": ["dotnet", "/opt/bookstudio/servers/quality/BookStudio.Mcp.Quality.dll"] },
    "book-production": { "type": "local", "command": ["dotnet", "/opt/bookstudio/servers/production/BookStudio.Mcp.Production.dll"] },
    "book-ops":        { "type": "local", "command": ["dotnet", "/opt/bookstudio/servers/ops/BookStudio.Mcp.Ops.dll"] }
  }
}
```

**Prerequisite:** .NET 10 runtime (`dotnet` on PATH). Install from https://dot.net/

In the reference above the `/opt/bookstudio` prefix is a placeholder; replace it
with the real absolute install path, then open OpenCode from that project folder
to connect the five servers.

---

## Reserved tools (not yet active)

These tool names are reserved for future phases and will be added without
breaking changes to the existing 10 active tools.

| Server | Reserved tools |
|---|---|
| book-core | `book.project.create`, `book.project.get_status`, `book.project.configure`, `book.decision.submit` |
| book-authoring | `book.plan.create`, `book.scene.generate`, `book.chapter.generate`, `book.manuscript.assemble` |
| book-quality | `book.repair.propose`, `book.repair.apply`, `book.memory.get`, `book.memory.commit` |
| book-production | `book.asset.register`, `book.render.preview`, `book.render.final`, `book.publish.package` |
| book-ops | `book.autopilot.start`, `book.autopilot.status`, `book.autopilot.pause`, `book.autopilot.resume`, `book.autopilot.cancel`, `book.autopilot.replay` |
