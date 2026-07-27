# VS-027 — Dual RED Evidence

## RED-I

`tests/governance/test_mcp_conformance_contract.py` requires components that do not yet exist:

- subprocess conformance project;
- versioned malformed-input corpus;
- deterministic fuzz runner;
- process driver with bounded timeouts;
- solution, architecture and CI registration.

Expected result: Governance fails because the required contracts are absent.

## RED-E

No executable currently launches all five bounded MCP servers and proves:

- common JSON-RPC error semantics;
- lifecycle recovery;
- malformed-input handling;
- deterministic fuzz survival;
- oversize recovery;
- no workspace creation, leak, crash or hang;
- clean EOF.

## Preservation rule

After RED confirmation, the test may change only through an approved TestChangeRequest. The five real subprocesses, malformed corpus, deterministic seed, no-crash/no-hang, no-leak, lazy workspace and EOF requirements cannot be weakened.
