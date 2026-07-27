# VS-026 — TestChangeRequest TCR-026-002

## Trigger

The initial RED governance test required every bounded router source file to contain direct `prompts/list`, `prompts/get` and `McpPromptDispatcher` references. During implementation, the shared protocol design showed that duplicating capability merging, prompt dispatch, resource pagination and prompt-resource reads in five routers would create five implementations of the same MCP behavior.

## Approved architecture

Introduce one shared decorator:

```text
PromptEnabledFeatureRouter
```

Each bounded server composition root wraps its existing verified router with:

- its `VersionedMcpPromptCatalog`;
- its existing listed resource definitions;
- its existing resource cursor scope and page size.

The decorator:

- adds `prompts.listChanged = false` to capabilities;
- dispatches `prompts/list` and `prompts/get` through `McpPromptDispatcher`;
- merges the prompt resource into `resources/list` with one opaque cursor contract;
- serves the prompt resource from the same versioned definition;
- delegates all tools, dynamic resource reads and resource templates to the original bounded router;
- owns disposal of the wrapped router.

## Governance test change

Replace the source-location assertion “every router contains prompt strings” with assertions that:

- the shared decorator contains capability, list/get dispatcher and resource merging;
- each of the five Program composition roots uses `PromptEnabledFeatureRouter` and its matching prompt catalog;
- subprocess conformance proves behavior in every server.

## Preserved requirements

- Every server advertises and executes prompts/list/get.
- Every server lists and reads its versioned prompt resource.
- Existing bounded routers retain their verified tool and dynamic-resource behavior.
- No sampling, models, egress or automatic tool execution.
- Existing resource cursor, lazy-runtime, security and EOF expectations remain tested.
- Prompt/resource parity remains mandatory.

## Test Auditor decision

**APPROVED** — the shared composition reduces duplicated protocol code while strengthening consistency. The change relocates implementation; it does not reduce any externally observable requirement.
