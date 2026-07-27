# VS-024 — Dual RED Evidence

## RED-I

The governance contract requires absent production components:

- provider-neutral release service and models;
- separate production MCP process;
- schemas, catalog, router and lazy runtime;
- integration executable;
- architecture and CI registrations.

Expected result: `test_book_production_contract.py` fails because the implementation is absent.

## RED-E

There is no process able to prepare an immutable release manifest or run preflight over verified sources. The authoring→production journey cannot be completed.

The failure is caused by missing product behavior, not environment, syntax or unrelated dependencies.

## Preservation rule

After RED confirmation, tests may change only through a documented TestChangeRequest. Active/reserved surface, immutable release, preflight, no-mutation and security assertions must not be weakened.
