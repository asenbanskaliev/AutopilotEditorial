# BookStudio.Tests.BookProduction Agent Rules

## Allowed

- Launch real authoring and production MCP child processes.
- Register source artifacts through authoring before production.
- Verify release preparation, immutable conflicts, preflight decisions and profile resources.
- Compare workspace inventory before and after preflight to prove read-only behavior.
- Use disposable workspaces and deterministic request/response chaining.

## Forbidden

- Do not seed release manifests directly as the only acceptance proof.
- Do not invoke production services or routers directly as the external GREEN gate.
- Do not use renderers, networks, models or mocks in the product journey.
- Do not weaken source-integrity, role-media, scope, version-conflict, path-leak or stdout assertions.
- Do not print source content, workspace paths or secrets.
