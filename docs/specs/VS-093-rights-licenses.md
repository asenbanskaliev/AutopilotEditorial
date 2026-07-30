# VS-093 — Rights and licenses

## Intent

Gestionar de forma durable, auditable y fail-closed los derechos y licencias necesarios para utilizar texto, imágenes, fuentes, datos y otros activos editoriales vinculados a una bibliografía `VS-092` aprobada, exacta y vigente.

## Behaviors

1. Un expediente de derechos declara workspace, proyecto, bibliografía autorizante, activo, titular, tipo de derecho, territorios, idiomas, canales, vigencia, restricciones, actor y evidencia causal exacta.
2. Solo una bibliografía `VS-092` aprobada, vigente y no stale puede autorizar la creación o decisión del expediente.
3. Cada activo queda identificado mediante tipo, referencia estable, digest y versión para impedir sustituciones silenciosas.
4. Las licencias admiten estados `PROPOSED`, `VALIDATED`, `APPROVED`, `REJECTED`, `EXPIRED`, `REVOKED` y `STALE`.
5. Ningún activo puede marcarse utilizable si faltan titular, alcance, territorio, canal, vigencia, evidencia o restricciones obligatorias.
6. Las decisiones son atribuibles y conservan actor, razón, timestamp y revisión esperada.
7. Drift en autoridad, activo, evidencia, alcance o vigencia marca el expediente `STALE` y bloquea su uso.
8. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
9. Concurrencia optimista, rollback atómico, recuperación tras reinicio, aislamiento por workspace e historial append-only son obligatorios.
10. Toda aprobación, rechazo, revocación, expiración o stale genera Outbox exactly-once.

## Invariants

- No existe expediente de derechos sin autoridad exacta desde `VS-092`.
- Un expediente no puede mezclar workspaces, proyectos, bibliografías, activos, titulares o snapshots.
- `APPROVED` exige cobertura completa y vigente del uso solicitado.
- Una transición fallida no deja licencias, decisiones, historial ni eventos parciales.
- Replay no duplica expedientes, historial ni eventos.
- Un expediente `EXPIRED`, `REVOKED` o `STALE` nunca autoriza uso editorial.

## Gates

- Autoridad exacta desde bibliografía aprobada.
- Activo, titular, licencia, alcance, territorios, canales y vigencia tipados.
- Validación fail-closed y decisiones atribuibles.
- Detección de expiración, revocación y drift.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Historial append-only y Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.
