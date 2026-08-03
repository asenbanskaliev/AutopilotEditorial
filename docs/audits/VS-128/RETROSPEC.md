# VS-128 RetroSpec

## Expected learning

A production-like MCP test must exercise the actual client and provider boundary, not only JSON-RPC fixtures.

## Design adjustments

- Use OpenCode's documented `OPENCODE_AUTH_CONTENT` path so credentials remain memory-only.
- Pin the CLI package instead of installing `latest`.
- Discover the live Zen catalogue but allow only explicitly free model IDs.
- Separate technical connection evidence from literary-quality claims.
- Treat absence of a secret as a failed live gate rather than a skipped success.

## Promotion rule

The slice is complete only after Plan Integrity, Governance Gates, both .NET CI workflows and OpenCode Live MCP Audit pass on one exact PR head with no open review threads.
