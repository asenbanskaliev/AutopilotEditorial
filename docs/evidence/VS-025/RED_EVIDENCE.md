# VS-025 — Dual RED Evidence

## RED-I

The governance contract requires absent operations components:

- provider-neutral Application diagnostics service and models;
- separate book-ops MCP process;
- schemas, catalog, router and lazy runtime;
- integration executable;
- solution, architecture and CI registrations.

Expected result: `test_book_ops_contract.py` fails because the implementation is absent.

## RED-E

There is no `BookStudio.Mcp.Ops` child process. A client cannot observe missing/ready SQLite readiness, compare the capability resource with diagnostics or prove that diagnostics leave the workspace unchanged.

Autopilot controls are deliberately unavailable because `AutopilotWorkflowRun + AutopilotJob`, scheduler and worker have not been implemented.

## Preservation rule

After RED confirmation, tests may change only through a documented TestChangeRequest. Active/reserved surface, real readiness, no-mutation, no-leak and subprocess assertions must not be weakened.
