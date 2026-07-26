# VS-021 — TestChangeRequest TCR-021-001

## Trigger

VS-020 deliberately asserted an empty initialize capability object because no optional MCP feature existed at that checkpoint. VS-021 introduces the first verified tools and resources surface.

## Requested change

Replace only the VS-020 subprocess assertion:

```text
initialize.capabilities is empty
```

with the cumulative acceptance assertion:

```json
{
  "tools": { "listChanged": false },
  "resources": { "subscribe": false, "listChanged": false }
}
```

The revised test must also assert that prompts, logging, completions, sampling, roots, tasks and experimental capabilities remain absent.

No lifecycle, negotiation, stdout, stderr, ID, error or EOF assertion may be removed or weakened.

## Justification

The original assertion represented the exact feature surface at VS-020, not a permanent prohibition on all capabilities. VS-021 canonically adds verified tools/resources, so retaining `{}` would make the earlier journey reject legitimate cumulative behavior.

## Impact

- VS-020 RED/GREEN evidence remains valid for its historical head.
- Initialize negotiation and lifecycle coverage remain unchanged.
- The updated assertion becomes stricter about the exact allowed cumulative surface.
- Any future capability addition requires another explicit TestChangeRequest.

## Test Auditor decision

**APPROVED** — the change follows planned capability progression and increases cumulative surface validation without dropping prior protocol coverage.
