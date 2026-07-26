# VS-012 — Auditoría M

## M1 — Specification Audit

**PASS**

- El puerto Application es neutral y no expone tipos SQLite.
- El alcance contiene solo infraestructura genérica: ledger de migrations y metadata.
- Outbox, jobs, proyectos y artefactos permanecen fuera de esta slice.
- WAL, foreign keys, timeout, single-writer, integrity y backup están definidos y probados.

## M2 — Implementation Audit

**PASS**

- `Microsoft.Data.Sqlite` está aislado en Infrastructure.
- `SqliteWorkspaceOptions` canonicaliza el workspace y rechaza nombres que escapan la raíz.
- `SqliteConnectionFactory` aplica configuración uniforme.
- El catálogo de migrations usa recursos embebidos, orden determinista y SHA-256.
- El runner aplica SQL y ledger en la misma transacción y bloquea cambios de hash.
- `SqliteWriteQueue` usa channel bounded, single reader, transacción por operación, rollback y drain.
- `SqliteWorkspaceDatabase` implementa lifecycle, metadata, health, WAL checkpoint y backup.
- El backup se serializa con escrituras y queda confinado al workspace.
- La migration inicial solo crea `workspace_metadata`; no anticipa entidades editoriales.

## M3 — Test Audit

**PASS**

- RED de contratos: Governance run `30210988726`, job `89817052567`.
- RED de supply chain: .NET run `30211366702`, job `89818031999`, bloqueado por `NU1903`.
- GREEN inicial: .NET run `30211514847`, job `89818422310`.
- GREEN endurecido: .NET run `30211707090`, job `89818923903`.
- GREEN Governance endurecido: run `30211707092`, job `89818924001`.
- Los tests prueban 64 escrituras concurrentes, cancelación, rollback, reinit idempotente, PRAGMAs, backup, path confinement, tamper de migration y uso tras dispose.

## M4 — Security and Operations Audit

**PASS**

- No se suprimió `NU1903`.
- `SQLitePCLRaw.bundle_e_sqlite3` y `SQLitePCLRaw.lib.e_sqlite3` están fijados en `2.1.12`, fuera del rango vulnerable `<= 2.1.11`.
- Las versiones están centralizadas y el restore quedó limpio.
- Backup fuera del workspace y sobre el origen se rechazan antes de borrar archivos.
- SQL de valores usa parámetros.
- Migration SQL es controlado, embebido y hasheado.
- El provider usa pooling, shared cache, WAL y busy timeout; las escrituras de la aplicación se serializan.
- La evidencia final tiene digest `sha256:1b728e64c4de03ab14c266328a341d9100b303d5af13c291021850b79301d265`.

## M5 — Product Flow Audit

**PASS**

```text
empty workspace
→ initialize SQLite
→ enable WAL and foreign keys
→ apply migration once
→ enqueue concurrent writes
→ cancel and roll back failures
→ quick_check
→ online backup inside workspace
→ open backup and verify contents
→ detect migration-ledger tamper
```

El journey se ejecuta desde un checkout limpio mediante `.NET CI`, sin mocks ni comandos ocultos.

## Meta-Audit

**PASS**

- Las pruebas estáticas, la compilación y la integración real coinciden.
- El fallo de supply chain se resolvió, no se silenció.
- Los controles adicionales surgidos de auditoría volvieron a ejecutarse en CI.
- No hay tablas, ports ni dependencias fuera del alcance aprobado.

## Verdict

`M_AUDIT_PASS`
