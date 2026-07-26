# VS-014-TEST-CHANGE-001 — Dynamic migration count

## Trigger

VS-014 adds the legitimate immutable migration `0002_outbox.sql`. The SQLite integration executable currently hard-codes `expectedMigrations: 1`, so it would fail even when the migration catalog and database are correct.

## Approved change

- Move the existing SQLite journey from top-level `Program.cs` to `SqliteJourney.cs`.
- Resolve expected count and latest version from `SqliteMigrationCatalog.Load()`.
- Keep every prior assertion: WAL, foreign keys, timeout, idempotent initialization, concurrent writes, cancellation, rollback, backup confinement, backup restore, migration-hash tamper and dispose behavior.
- Replace only the brittle literal, not the required behavior.

## Non-regression

The new harness must fail if a migration is missing, duplicated, unapplied or hash-mismatched. No assertion is removed and Outbox-specific tests remain separate.

## Status

APPROVED
