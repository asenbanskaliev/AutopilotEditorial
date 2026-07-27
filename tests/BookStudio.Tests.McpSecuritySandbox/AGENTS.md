# BookStudio.Tests.McpSecuritySandbox Agent Rules

## Allowed

- Launch all five real MCP executables through `dotnet` and stdio.
- Verify invalid host roots and quota options fail closed before protocol startup.
- Read the effective sandbox policy through MCP resources without activating product runtimes.
- Exercise the real filesystem Artifact Store for per-artifact, byte and file quotas.
- Verify rejected writes leave no manifest, do not consume a version and clean temporary files.
- Use only isolated temporary directories and deterministic payloads.

## Forbidden

- Do not mock MCP processes, host options, filesystem policy or Artifact Store behavior.
- Do not weaken root, symbolic-link, traversal, quota, no-leak or EOF checks.
- Do not use network access, external services, models or nondeterministic data.
- Do not write outside the journey-owned temporary root.
- Do not expose absolute workspace paths in test output or MCP assertions.
