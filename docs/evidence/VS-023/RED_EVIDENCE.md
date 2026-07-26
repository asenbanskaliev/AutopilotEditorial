# VS-023 — Dual RED Evidence

## RED-I

The governance contract requires absent quality components:

- provider-neutral Application service and models;
- separate quality MCP process;
- quality schemas, catalog, router and lazy runtime;
- integration executable;
- architecture and CI registrations.

Expected result: `test_book_quality_contract.py` fails because the implementation does not yet exist.

## RED-E

There is no `BookStudio.Mcp.Quality` child process. A client cannot audit a draft, evaluate a quality gate, read a quality profile or prove cross-server authoring→quality flow.

This is a product-behavior failure, not an environment or syntax failure.

## Preservation rule

After RED confirmation, tests may change only through a TestChangeRequest. Active/reserved surface, subprocess, no-mutation and security assertions must not be weakened.
