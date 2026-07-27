# VS-026 — RetroSpec

## Implemented contract

BookStudio now exposes versioned MCP prompts and canonical prompt resources through all five bounded servers.

Supported methods:

```text
prompts/list
prompts/get
resources/list
resources/read
```

Every server advertises exactly:

```json
{
  "prompts": {"listChanged": false},
  "resources": {"subscribe": false, "listChanged": false},
  "tools": {"listChanged": false}
}
```

## Public prompt catalog

| Server | Prompt | Resource |
|---|---|---|
| book-core | `book.core.inspect-artifact.v1` | `book://prompts/book-core/inspect-artifact/v1` |
| book-authoring | `book.authoring.validate-draft.v1` | `book://prompts/book-authoring/validate-draft/v1` |
| book-quality | `book.quality.assess-draft.v1` | `book://prompts/book-quality/assess-draft/v1` |
| book-production | `book.production.preflight-release.v1` | `book://prompts/book-production/preflight-release/v1` |
| book-ops | `book.ops.inspect-readiness.v1` | `book://prompts/book-ops/inspect-readiness/v1` |

Names, versions and URIs are explicit and validated for parity.

## Shared protocol design

### `VersionedMcpPrompt`

Owns:

- explicit public name;
- numeric version;
- immutable resource URI;
- title and description;
- sorted argument definitions;
- trusted message template;
- bounded validated renderer;
- typed `McpGetPromptResult`;
- canonical JSON resource;
- resource metadata.

A name must end in `.v{version}` and its bounded context/action must match the resource URI ending in `/v{version}`.

### `VersionedMcpPromptCatalog`

Owns:

- deterministic ordinal ordering;
- duplicate-name and duplicate-URI rejection;
- name/URI lookup;
- prompt definitions;
- prompt resources;
- stable cursor fingerprint and scope.

### `McpPromptDispatcher`

Owns:

- `prompts/list`;
- `prompts/get`;
- opaque cursor decoding;
- strict parameter shape;
- bounded string argument parsing;
- unknown prompt rejection;
- safe `-32602` errors.

### `PromptEnabledFeatureRouter`

Decorates an existing bounded router and:

- adds prompts capability;
- dispatches prompt methods;
- merges prompt resources with existing static resources;
- serves prompt resource JSON;
- preserves dynamic resource reads and all existing tools;
- owns disposal of the wrapped router.

## Prompt behavior

### Core

Guides bounded use of `book.artifact.get` and optional explicit-version comparison without inventing missing content.

### Authoring

Guides `book.draft.validate`, explains structural metrics/warnings and forbids implicit version registration or claims of full linguistic editing.

### Quality

Guides `book.audit.run` followed by `book.gate.evaluate` with `draft-basic`, preserving pass/warn/fail and PASS/BLOCKED semantics without reserved repair/memory calls.

### Production

Guides `book.preflight.run` with `release-basic`, reports checks/blocking reasons and avoids claims of complete KDP compliance or reserved render/package/publish calls.

### Operations

Guides read-only status followed by diagnostics when needed, explains available/reserved capabilities and avoids reserved Autopilot controls.

## Resource contract

Each prompt resource uses:

```text
application/vnd.bookstudio.prompt-template+json
```

The resource contains:

- schemaVersion;
- promptVersion;
- name;
- title;
- description;
- arguments;
- user message template.

The resource and prompts/list/get are produced from the same object; no parallel hand-written resource exists.

## Validation and bounds

- prompt page size: 20;
- prompt name: 128 characters maximum;
- argument count: 16 maximum;
- argument name: 64 characters maximum;
- argument value: 256 characters maximum;
- rendered message: 4096 characters maximum;
- only string argument values;
- no additional arguments;
- project/draft/release scopes validated;
- versions positive and canonical;
- control characters rejected.

## Integration journey

The new executable launches the five real MCP processes and verifies:

```text
initialize exact capability set
→ prompts/list exact v1
→ prompts/get valid
→ prompt resource discover/read
→ list/get/resource parity
→ missing argument rejection
→ extra argument rejection
→ invalid scope/version rejection
→ unknown prompt rejection
→ lazy workspace unchanged
→ EOF
```

Existing MCP initialize, core, authoring, quality, production and ops journeys were updated through approved TCRs and all remain green.

## Security characteristics

- prompts are static trusted templates;
- user arguments remain bounded data inserted after validation;
- prompts do not execute tools;
- no sampling or model invocation;
- no egress, shell, roots, completions or tasks;
- no artifact content, paths or secrets are embedded;
- stdout remains JSON-RPC only;
- prompt retrieval does not create or mutate a workspace.

## TestChangeRequests

- `TCR-026-001`: third capability and merged resource pagination.
- `TCR-026-002`: shared decorator composition.
- `TCR-026-003`: dispatcher validation separated from typed rendering.

No functional or security expectation was removed.

## Deviations

- Exactly one prompt v1 is active per bounded server; broader prompt catalogs belong to later product journeys.
- Prompts return guidance only and rely on the client/LLM to choose and sequence tool calls.
- Agent-specific prompt profiles and model compatibility belong to F3.
- No prompt change notification is emitted because catalogs are immutable for the process lifetime.

## Follow-on constraints

- Incompatible edits require v2 names and URIs.
- Future prompts must remain bounded-context specific and reference only active, truthful capabilities.
- New prompt resources must continue to derive from the same definition as list/get.
- Prompts must never become hidden autonomous execution paths.
- Later agent profiles may select prompts but must not bypass argument, scope or resource-parity rules.

## Next slice

`VS-027 — MCP conformance`.
