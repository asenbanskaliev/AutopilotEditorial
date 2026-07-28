# VS-063 — Transition audit

## Intent

Auditar transiciones entre párrafos, escenas y capítulos usando artefactos aprobados exactos, de modo que continuidad temporal, espacial, causal, tonal y de estado quede demostrada antes de avanzar.

## Behaviors

1. La auditoría se liga a dos unidades aprobadas exactas mediante IDs, versiones y digests inmutables.
2. Soporta scopes `PARAGRAPH`, `SCENE` y `CHAPTER`.
3. Registra estado de salida de la unidad origen y estado de entrada de la unidad destino.
4. Evalúa tiempo, ubicación, personajes, objetos, conocimiento, objetivo, tono y causalidad.
5. Cada dimensión obtiene `SUPPORTED`, `PARTIAL`, `BROKEN` o `NOT_APPLICABLE` con evidencia atribuible.
6. Los hallazgos son append-only, versionados y clasificados por severidad.
7. El ciclo es `DRAFT → RUNNING → REVIEWED → CLOSED`.
8. El cierre se bloquea mientras existan dimensiones rotas o hallazgos bloqueantes abiertos.
9. Replay exacto es idempotente; reutilización conflictiva falla cerrada.
10. Concurrencia optimista, aislamiento por workspace y recuperación tras reinicio son obligatorios.
11. El cierre emite exactamente un evento `editorial.transition-audit.closed` mediante Outbox atómico.

## Gates

- Autoridad exacta de origen y destino.
- Matriz completa de dimensiones de transición.
- Rangos y evidencia estables.
- Decisiones gobernadas y cierre bloqueante.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.
