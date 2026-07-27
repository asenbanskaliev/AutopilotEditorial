# VS-026 — Dual RED Evidence

## RED-I

The governance contract requires absent prompt components:

- shared prompt models, versioned template/catalog, argument rules and dispatcher;
- one prompt catalog for each of the five bounded servers;
- prompt capability and list/get dispatch in every router;
- versioned prompt resources;
- dedicated prompts/resources integration executable;
- solution, architecture and CI registrations.

Expected result: `test_prompts_resources_contract.py` fails because these contracts do not yet exist.

## RED-E

The five current processes advertise only tools/resources. `prompts/list` and `prompts/get` return Method not found, and no versioned prompt resource can be listed or read.

## Preservation rule

After RED confirmation, tests may change only according to `TCR-026-001`. Existing tool surfaces, resource security, lazy composition, stdout/stderr and EOF requirements must remain intact.
