# VS-028 — Dual RED Evidence

## RED-I

The security governance contract requires absent components:

- shared workspace sandbox admission;
- strict host quota options;
- sandbox policy resource/decorator;
- provider-neutral global quota exception;
- Artifact Store bytes/files enforcement;
- runtime wiring across five servers;
- subprocess security integration;
- solution, architecture and CI registration.

Expected result: Governance fails because these contracts are absent.

## RED-E

The current MCP host accepts a filesystem root or an existing linked workspace if it can be canonicalized. Artifact Store limits one artifact but does not cap total store bytes or file count. There is no process journey proving atomic quota rejection or version preservation.

## Preservation rule

After RED confirmation, tests may change only through an approved TestChangeRequest. Mandatory properties cannot be weakened:

- all five host processes;
- root/file/symlink/parent-symlink rejection;
- individual and global quotas;
- rejected writes consume no version;
- no path leak or outside file;
- effective policy resource;
- existing MCP conformance remains PASS.
