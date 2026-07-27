# VS-024 — TestChangeRequest TCR-024-001

## Trigger

The first complete external GREEN run reached `book.release.prepare` and failed the test-only `NoLeaks` assertion. The assertion rejected every occurrence of the substring `.bookstudio` in an MCP response.

The production response legitimately includes the registered media type:

```text
application/vnd.bookstudio.release-manifest+json
```

This is a logical IANA-style vendor media type, not a filesystem path. The assertion therefore produced a false positive before preflight behavior could be evaluated.

## Requested change

Replace only the broad substring assertion:

```text
response must not contain `.bookstudio`
```

with path-specific assertions:

```text
response must not contain `/.bookstudio/`
response must not contain the JSON-escaped Windows path segment `\\.bookstudio\\`
```

The existing assertion that rejects the complete disposable workspace root remains unchanged. Assertions rejecting source content, stderr leaks, unexpected stdout and physical path disclosure also remain unchanged.

## Justification

`bookstudio` is part of the canonical release-manifest media type and must remain observable so clients can identify the artifact contract. A filesystem leak requires path separators and path context; the revised assertions distinguish the legitimate media type from Linux and JSON-escaped Windows store paths.

The change does not alter production code, expected tool behavior, release immutability, preflight checks, source integrity, scope protection or reserved-tool rejection.

## Impact

- The original failed run remains valid evidence of a harness false positive.
- Physical workspace paths are still rejected by the full-root assertion.
- Linux `.bookstudio` directory segments remain rejected.
- JSON-escaped Windows `.bookstudio` directory segments remain rejected.
- Source content and diagnostics remain protected.
- No functional acceptance criterion is removed or weakened.

## Test Auditor decision

**APPROVED** — the revised check preserves the security intent while allowing the canonical `application/vnd.bookstudio.release-manifest+json` media type required by the production contract.
