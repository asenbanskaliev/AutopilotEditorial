# VS-091 RetroSpec

## Capability delivered

Verificación durable y gobernada de claims a partir de un plan de investigación `VS-090` aprobado y vigente.

## Specification synchronized after implementation

- La identidad causal incluye workspace, proyecto, plan de investigación, claim, versión, actor y request fingerprint.
- La evidencia conserva fuente, localización, vigencia, cobertura, confianza, estado y payload reproducible.
- Las decisiones quedan bloqueadas mientras exista evidencia abierta, incompleta o insuficiente.
- Drift de autoridad marca la verificación `STALE` mediante transición explícita.
- Replay exacto es idempotente; reutilización conflictiva falla comparando payload real.
- Historial, receipts, estado y Outbox se persisten atómicamente.
- Reinicio y aislamiento por workspace son parte del comportamiento contractual.

## Residual risks

- La calidad factual de fuentes externas pertenece a slices posteriores de citas, bibliografía y derechos.
- Los adaptadores remotos no forman parte de este slice; el boundary actual es durable y extensible.

## Exit condition

VS-091 solo puede fusionarse cuando Plan Integrity, Governance Gates y `.NET CI` estén en PASS sobre el mismo head documental final.
