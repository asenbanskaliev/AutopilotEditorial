# VS-128 M Audit

## Claim under audit

The live workflow can prove real OpenCode Zen authentication and real AutopilotEditorial MCP discovery/tool routing without persisting or leaking the repository API key.

## Independent checks

- The secret enters only through the workflow environment.
- The workflow masks the secret before any live command.
- The runner uses `OPENCODE_AUTH_CONTENT`; it does not create `auth.json`.
- OpenCode is version-pinned and installed into a disposable prefix.
- The MCP is published from the pull-request source and registered as a local stdio server.
- Model selection is restricted to an explicit free-model allowlist.
- Missing secret, missing free model, disconnected MCP, failed invocation and leakage all fail closed.
- Evidence stores hashes, booleans and durations only.

## Residual risks

- OpenCode or Zen may change external behavior while the pinned package remains available.
- A model can return an unexpected response despite a healthy MCP connection.
- This smoke audit does not measure long-book literary quality.

## Decision

Implementation is eligible for validation. Capability promotion and merge require the live workflow plus all repository gates to pass on one exact head.
