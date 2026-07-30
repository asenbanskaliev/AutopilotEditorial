# VS-093 — RetroSpec

## Qué cambió respecto al supuesto inicial

La slice comenzó como una especificación mínima. La implementación confirmó que derechos y licencias no podían modelarse como una simple bandera: requerían autoridad causal exacta desde `VS-092`, alcance multidimensional, vigencia, restricciones, decisiones atribuibles, estados terminales y recuperación durable.

## Decisiones consolidadas

- Un expediente fija el snapshot de autoridad por revisión y digest exactos.
- Territorios, idiomas y canales forman parte del alcance gobernado, no de metadatos informales.
- La evidencia y las restricciones son obligatorias para aprobar.
- Revocación, expiración y stale son estados explícitos y auditables.
- Replay exacto es idempotente; reutilización conflictiva falla cerrada.
- Historial append-only y Outbox exactly-once son parte del comportamiento, no infraestructura opcional.

## Aprendizajes

- Las pruebas acumulativas deben sembrar todas las columnas obligatorias de la autoridad precedente para detectar drift de esquema.
- La evidencia funcional debe separar el head de implementación del head documental final y exigir una nueva ejecución completa.
- Los riesgos jurídicos externos deben quedar declarados sin convertirlos en garantías técnicas inexistentes.

## RetroSpec final

La especificación `docs/specs/VS-093-rights-licenses.md` refleja los comportamientos e invariantes implementados. No se identifican requisitos retroactivos sin cobertura dentro del alcance de la slice.

Resultado: RETROSPEC PASS, sujeto a los tres workflows verdes sobre el head final.
