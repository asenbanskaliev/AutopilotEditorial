# VS-028 — TestChangeRequest TCR-028-001

## Trigger

The sandbox slice adds one intentional public resource to every bounded MCP server:

```text
book://security/sandbox-policy
```

The five cumulative journeys encoded the exact number of resource pages from VS-026. Their failures are therefore expected contract drift caused by the approved catalog extension, not product regressions.

## Approved test change

- Continue requiring every previously exposed schema, profile and prompt resource.
- Add the sandbox policy URI and media type to each server expectation.
- Traverse resource pagination until `nextCursor` is absent instead of asserting a fixed number of pages.
- Preserve exact ordering, unique URIs, cursor validation, read behavior, tools, security, no mutation, lazy workspace and EOF checks.

## Preserved requirements

- no prior resource may disappear or change identity;
- the new policy resource must be present exactly once;
- all resource pages must remain ordered and cursor-bound;
- invalid cursors and invalid reads remain rejected;
- all previous product journeys and mutation guarantees remain intact.

## Test Auditor decision

**APPROVED** — the cumulative expectations are extended for one authorized resource without reducing any observable requirement.
