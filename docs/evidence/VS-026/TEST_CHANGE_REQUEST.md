# VS-026 — TestChangeRequest TCR-026-001

## Trigger

The verified bounded-server journeys currently require initialize capabilities to contain exactly tools/resources and explicitly reject prompts. Several journeys also assert exact resource page sizes.

VS-026 intentionally adds MCP 2025-11-25 prompt support and one versioned prompt resource to each of the five verified servers. The existing assertions would therefore reject the new required contract even when tools, resources, lazy composition and security remain correct.

## Approved changes

Update cumulative integration tests to:

- require capabilities exactly `prompts`, `resources`, `tools`;
- require `prompts.listChanged = false`;
- remove prompts from forbidden capability lists;
- adjust resource page counts only where the versioned prompt resource changes the catalog size;
- preserve every existing tool name, annotation, resource schema, confinement, lazy workspace, stdout, stderr and EOF assertion;
- add prompt list/get/resource checks either in the existing journey or the dedicated VS-026 conformance journey.

## Non-approved changes

- Removing tools/resources capability checks.
- Relaxing tool or resource contracts.
- Allowing sampling, logging, completions, roots, tasks or experimental capabilities.
- Removing lazy-runtime/no-path/no-secret/EOF assertions.
- Hiding invalid prompt argument or unknown prompt failures.
- Changing active or reserved tool surfaces.

## Justification

Prompts are an additive MCP server feature controlled by the user/client. Exact capability and resource cardinality assertions must evolve when the public protocol surface intentionally expands. The change increases coverage and does not weaken previous product or security guarantees.

## Test Auditor decision

**APPROVED** — cumulative tests may be updated only within the boundaries above after the VS-026 RED contracts are captured.
