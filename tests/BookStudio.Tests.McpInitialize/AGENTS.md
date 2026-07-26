# MCP Initialize Integration Test Instructions

## Allowed

- Launch the real `BookStudio.Mcp` assembly as a child `dotnet` process.
- Communicate through redirected stdin, stdout and stderr.
- Send malformed and adversarial synthetic JSON-RPC inputs.
- Verify current, legacy and unsupported protocol-version negotiation.
- Close stdin and require clean process exit.

## Forbidden

- Invoking the session handler directly as the only evidence.
- Writing to or reading from network sockets.
- Fixed delays or sleeps.
- Accepting banners, logs or non-JSON content on stdout.
- Echoing request payloads, tokens or arbitrary content to stderr.

Every child process must be terminated in `DisposeAsync` when the test fails, and the test process must exit non-zero on regression.
