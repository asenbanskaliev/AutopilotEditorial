# BookStudio.Tests.BookQuality Agent Rules

## Allowed

- Launch the real authoring and quality MCP child processes.
- Use one disposable shared workspace to prove cross-server interoperability.
- Register drafts through authoring stdio JSON-RPC before quality assessment.
- Verify quality identity, capabilities, tools, profile resource, audits and gate decisions.
- Compare workspace file inventory before and after quality to prove read-only behavior.
- Chain requests and responses deterministically without fixed sleeps.

## Forbidden

- Do not seed the Artifact Store directly as the only acceptance proof.
- Do not invoke quality services or routers directly as the external GREEN gate.
- Do not use model output, network calls or mocks in the product journey.
- Do not weaken reserved-tool, scope, no-mutation, path-leak or stdout assertions.
- Do not print draft text, workspace paths or secrets in diagnostics.
