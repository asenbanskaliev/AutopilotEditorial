# VS-001 — TestChangeRequest TCR-001

## Trigger

After completing DUAL_GREEN, Auditoría M and RetroSpec, `SLICE_STATUS.csv` advanced `VS-001` from `IN_PROGRESS` to `VERIFIED`.

The governance test still asserted the intermediate state `IN_PROGRESS`, so the latest CI run failed after all plan and completion validations had passed.

## Requested change

Replace the intermediate-state assertion with the final acceptance state:

- `VS-000 == VERIFIED`
- `VS-001 == VERIFIED`

No other assertion may be removed or weakened.

## Justification

The behavior under test is state progression, not permanent retention of the implementation state. The slice Definition of Done requires `VERIFIED` before merge.

## Impact

- RED evidence remains valid for the initial missing behavior.
- Dependency, uniqueness, wave coverage and status-reference tests remain unchanged.
- `next_slice.py` can now expose `VS-002` as READY.

## Test Auditor decision

**APPROVED** — the change aligns the test with the approved final state and does not reduce coverage.
