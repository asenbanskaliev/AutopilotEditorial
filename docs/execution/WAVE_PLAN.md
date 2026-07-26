# Wave Plan

GitHub issues are created and activated by waves. The complete immutable scope remains in `docs/master-plan/full-program-backlog.csv`; mutable execution state is stored in `docs/execution/SLICE_STATUS.csv`.

## Wave 0 — Bootstrap

- F0-BOOTSTRAP: VS-000 to VS-002.

## Wave 1 — Foundation

- F1-FOUNDATION: VS-010 to VS-016.

## Wave 2 — MCP

- F2-MCP: VS-020 to VS-028.

## Wave 3 — OpenCode and Autopilot

- F3-OPENCODE: VS-030 to VS-035.
- F4-AUTOPILOT: VS-040 to VS-047.

## Wave 4 — Authoring and Coherence

- F5-AUTHORING: VS-050 to VS-055.
- F6-COHERENCE: VS-060 to VS-070.

## Wave 5 — Professional quality and rights

- F7-PROFESSIONAL: VS-080 to VS-088.
- F8-RESEARCH-RIGHTS: VS-090 to VS-095.

## Wave 6 — Visual and Production

- F9-VISUAL: VS-100 to VS-105.
- F10-PRODUCTION: VS-110 to VS-118.

## Wave 7 — Operations and Enterprise

- F11-OPERATIONS: VS-120 to VS-126.
- F12-ENTERPRISE: VS-130 to VS-137.

## Wave 8 — Certification

- F13-CERTIFICATION: VS-140 to VS-148.

## Activation rule

A slice may have an issue before it is READY, but it becomes READY only when every dependency is `VERIFIED`, `RELEASED` or `EXCLUDED_BY_CONTRACT`.

## Reconciliation rule

After every merge:

1. update `SLICE_STATUS.csv`;
2. close the completed issue;
3. resolve the next READY slice;
4. create or activate the next issue;
5. update `EXECUTION_STATUS.md`.
