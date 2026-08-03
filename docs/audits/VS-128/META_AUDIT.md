# VS-128 Meta-Audit

The audit avoids self-certification by requiring externally executed GitHub Actions evidence from the real OpenCode CLI and Zen service. Static governance tests only verify wiring and safety invariants; they cannot substitute for the live workflow.

False-positive controls:

- missing secret is failure, not skip;
- unavailable free model is failure, not paid fallback;
- model invocation must return exit code zero;
- MCP must be connected before and rediscovered after the invocation;
- evidence creation occurs only after leakage scanning;
- merge requires every gate on the exact final head.
