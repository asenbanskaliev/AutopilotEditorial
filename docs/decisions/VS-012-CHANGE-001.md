# VS-012 ChangeRequest 001 — Secure SQLite native dependency

## Trigger

.NET CI run `30211366702`, job `89818031999`, failed restore/build because `Microsoft.Data.Sqlite 10.0.10` resolved `SQLitePCLRaw.lib.e_sqlite3 2.1.11`, which NuGet flags with high-severity advisory `GHSA-2m69-gcr7-jv3q`.

## Decision

Keep `Microsoft.Data.Sqlite 10.0.10` and centrally pin the compatible SQLitePCLRaw 2.x patch line:

- `SQLitePCLRaw.bundle_e_sqlite3` `2.1.12`;
- `SQLitePCLRaw.lib.e_sqlite3` `2.1.12`.

## Rejected alternatives

- Suppress `NU1903`: rejected because it would hide a known high-severity dependency.
- Disable warnings-as-errors: rejected because it weakens the repository-wide supply-chain gate.
- Move directly to SQLitePCLRaw 3.x: deferred because it is a major-version change not required to repair this slice.

## Impact

- No Application or database behavior changes.
- Restore should resolve a native SQLite build newer than the affected `<= 2.1.11` range.
- Governance tests will assert the secure transitive pins.
- Future dependency updates remain subject to vulnerability scanning and integration tests.

## Approval

**APPROVED** — minimal compatible security remediation with no warning suppression.
