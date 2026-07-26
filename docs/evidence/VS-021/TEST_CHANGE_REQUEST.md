# VS-021 — TestChangeRequest TCR-021-001

## Trigger

VS-020 deliberately asserted an empty initialize capability object because no optional MCP feature existed at that checkpoint. It also used `tools/list` as an arbitrary unknown ready-state method. VS-021 introduces the first verified tools/resources surface, so both assumptions become historical rather than cumulative.

## Requested change

### Capability assertion

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

### Unknown-method assertion

Replace the ready-state request method used solely to prove `Method not found`:

```text
tools/list
```

with a permanently unregistered synthetic method:

```text
unknown/method
```

The expected JSON-RPC code remains `-32601`. The pre-initialize `tools/list` request remains unchanged and must still return `-32002`.

No lifecycle, negotiation, stdout, stderr, ID, error-code or EOF assertion may be removed or weakened.

## Justification

The original assertions represented the exact feature surface at VS-020, not a permanent prohibition on capabilities or on implementing `tools/list`. VS-021 canonically adds verified tools/resources. Keeping the old method name would turn a valid cumulative feature into a false regression, while the synthetic unknown method preserves the exact protocol behavior being tested.

## Impact

- VS-020 RED/GREEN evidence remains valid for its historical head.
- Initialize negotiation and lifecycle coverage remain unchanged.
- The updated capability assertion is stricter about the exact allowed cumulative surface.
- Method-not-found coverage remains identical and no longer collides with planned methods.
- Any future capability addition requires another explicit TestChangeRequest.

## Test Auditor decision

**APPROVED** — the changes follow planned capability progression, preserve all protocol assertions and increase cumulative surface validation.
