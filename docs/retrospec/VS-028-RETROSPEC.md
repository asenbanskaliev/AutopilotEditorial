# VS-028 — RetroSpec

## Implemented contract

BookStudio MCP now applies one shared strict-local sandbox policy to all bounded servers:

```text
BookStudio.Mcp
BookStudio.Mcp.Authoring
BookStudio.Mcp.Quality
BookStudio.Mcp.Production
BookStudio.Mcp.Ops
```

The policy is enforced before JSON-RPC startup and again at Artifact Store path/write boundaries.

## Host admission

`McpWorkspaceSandboxPolicy` requires a workspace root that is:

- non-empty and free of control characters;
- canonicalizable to a local filesystem path;
- not a filesystem root;
- not an existing file;
- not UNC or a Windows device path;
- free of existing symlinks and reparse points in every existing ancestor.

An invalid host configuration:

- exits with code `2`;
- writes no stdout;
- writes exactly `MCP_INVALID_HOST_OPTIONS` to stderr;
- never starts the MCP lifecycle.

## Effective limits

MCP host defaults:

```text
maximumArtifactBytes = 16777216
maximumStoreBytes    = 1073741824
maximumStoreFiles    = 100000
```

Options:

```text
--max-artifact-bytes
--max-store-bytes
--max-store-files
```

Rules:

- values are positive canonical decimal integers;
- leading-zero alternate forms are rejected;
- `maximumStoreBytes >= maximumArtifactBytes`;
- each option may be configured only once;
- environment/default workspace admission uses the same policy.

## Public policy resource

Every MCP server exposes:

```text
book://security/sandbox-policy
```

Media type:

```text
application/vnd.bookstudio.sandbox-policy+json
```

The resource contains:

- schema version;
- mode `strict-local`;
- effective artifact/store/file limits;
- workspace rules;
- store rules.

It never contains the physical workspace path. Reading it is static and does not create the workspace.

## Resource composition

`SandboxEnabledFeatureRouter` adds the policy definition to the bounded resource catalog and delegates all non-policy behavior.

The final composition remains:

```text
bounded feature router
→ sandbox resource decorator
→ prompt/resource decorator
→ McpSession
→ stdio transport
```

Resources remain:

- ordinally sorted;
- unique by URI;
- cursor-fingerprinted;
- fully paginated until `nextCursor` is absent.

## Artifact Store quotas

`FileArtifactStoreOptions` now contains:

- `MaximumArtifactBytes`;
- `MaximumStoreBytes`;
- `MaximumStoreFiles`;
- `BufferSize`.

Provider defaults remain larger than MCP defaults for non-MCP consumers:

```text
artifact = 256 MiB
store    = 4 GiB
files    = 250000
```

MCP runtimes pass their stricter effective limits explicitly.

## Transactional write contract

A write follows:

```text
validate request
→ write/hash temporary content with individual limit
→ acquire global write gate
→ acquire artifact lock
→ validate expected immutable version
→ serialize bounded manifest
→ detect whether content blob already exists
→ measure only permanent blob + manifest usage
→ project exact additional bytes/files
→ reject or promote content blob
→ publish manifest atomically
→ rollback newly promoted blob if manifest publish fails
→ clean temporary content
```

Permanent usage excludes the temp directory.

Projected delta:

- new content hash: blob bytes + manifest bytes, two files;
- existing deduplicated hash: manifest bytes, one file.

## Error contract

Provider-neutral exception:

```text
ArtifactStoreQuotaExceededException
```

Dimensions:

- `bytes`;
- `files`.

It contains limit and observed projection for internal handling.

MCP-facing authoring/production map it to:

```text
artifact_store_quota_exceeded
```

No physical path or internal filesystem detail is returned.

## Rejection invariants

A rejected write must not:

- publish a manifest;
- leave an unreferenced newly promoted blob;
- leave temporary files;
- consume the expected immutable version;
- affect an existing deduplicated blob;
- write outside `.bookstudio/artifacts`.

After a rejected version 2, a later valid version 2 write must succeed.

## Security journey

Project:

```text
tests/BookStudio.Tests.McpSecuritySandbox
```

It launches all five real MCP executables and separately exercises the real filesystem Artifact Store.

Process matrix verifies:

```text
5 servers
× filesystem root rejection
× existing file rejection
× invalid byte relationship
× non-canonical file quota
+ symlink rejection where supported
```

Successful process path verifies:

```text
initialize
→ initialized
→ complete resource pagination
→ policy discovery/read
→ exact effective limits
→ no path leak
→ lazy workspace
→ EOF
```

Provider path verifies five quota/security groups:

1. individual artifact limit;
2. traversal rejection;
3. file quota projection;
4. byte quota and version preservation;
5. deduplicated file quota.

## Verified result

```text
MCP_SECURITY_SANDBOX_PASS servers=5 invalidStarts=25 policyReads=5 quotaChecks=5
```

Exit code is 0 and stderr is empty.

## CI contract

```text
dotnet.mcp-security-sandbox-integration
```

Evidence:

```text
artifacts/ci/dotnet-mcp-security-sandbox-integration.json
```

The journey runs after generic MCP conformance. Both must remain green.

## TestChangeRequest

`TCR-028-001` authorizes the additional sandbox policy resource in every cumulative server catalog. Fixed page-count tests were replaced with complete cursor traversal while preserving all previous resource identities and behavioral assertions.

## Deviations

- Quota coordination is intra-process, not a cross-process filesystem lock.
- Usage is measured by scanning permanent store trees rather than a durable usage index.
- Sandbox admission/path checks do not replace OS-level process isolation.
- Symlink creation tests are conditional when the runner/platform does not permit symlink creation; policy code is still statically and functionally covered on supported runners.

## Follow-on constraints

- New MCP servers must use the same host admission and policy resource.
- New Artifact Store providers must implement equivalent quota and rollback semantics.
- Multi-process writers require a durable/interprocess quota reservation mechanism before production scale-out.
- No future optimization may count temp files as permanent quota or publish before quota admission.
- Security policy changes require a new schema version or an explicit backwards-compatible extension.

## Phase result

`VS-028` completes the planned F2-MCP slice sequence. The full product remains `NOT_READY`; subsequent phases must preserve MCP conformance and sandbox gates.
