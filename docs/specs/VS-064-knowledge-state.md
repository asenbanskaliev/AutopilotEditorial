# VS-064 — Knowledge state

## Intent

Persistir y validar el conocimiento narrativo derivado de transiciones y escenas cerradas, distinguiendo hechos objetivos, creencias por sujeto y secretos con alcance explícito.

## Behaviors

1. La autoridad causal requiere una auditoría de transición cerrada exacta.
2. Cada entrada se clasifica como `FACT`, `BELIEF` o `SECRET`.
3. Las entradas conservan sujeto, objeto, afirmación normalizada, evidencia, vigencia y procedencia.
4. Los hechos no pueden contradecir otros hechos activos sin hallazgo bloqueante.
5. Las creencias pueden divergir de los hechos y entre sujetos, pero deben registrar perspectiva.
6. Los secretos declaran conocedores y excluidos; una divulgación actualiza el alcance mediante evento append-only.
7. El ciclo es `DRAFT → ACTIVE → SUPERSEDED|RETRACTED`.
8. Replay exacto es idempotente; reutilización conflictiva falla cerrada.
9. Concurrencia optimista, aislamiento por workspace y recuperación tras reinicio son obligatorios.
10. Activación y divulgación emiten Outbox exactly-once.

## Gates

- Autoridad exacta desde transición cerrada.
- Contradicciones y divulgaciones gobernadas.
- Historial append-only y estado materializado reproducible.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.
