# VS-022 — Dual RED Evidence

## RED-I

The governance contract intentionally requires files and CI contracts that do not yet exist:

- `IDraftAuthoringService`;
- `DraftAuthoringService` and models;
- separate `BookStudio.Mcp.Authoring` executable;
- authoring catalog, schemas, runtime and router;
- integration executable;
- architecture and CI registrations.

Expected failure: `tests/governance/test_book_authoring_contract.py` fails because the implementation is absent.

## RED-E

There is no `BookStudio.Mcp.Authoring` process. A client cannot:

- initialize an authoring-specific server identity;
- list `book.draft.register` or `book.draft.validate`;
- register an immutable draft;
- validate a stored draft;
- read an authoring draft resource.

The failure is caused by missing product behavior, not syntax, environment or unrelated dependencies.

## Preservation rule

After RED confirmation, tests may change only through a documented TestChangeRequest. Production code must make these existing expectations pass without removing active, reserved, security or subprocess assertions.
