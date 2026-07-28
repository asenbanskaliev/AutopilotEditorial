# VS-050 Auditoría M

Status: PASS

- La identidad del proyecto es estable y no depende del proveedor.
- La creación es idempotente por request ID y fingerprint inmutable.
- El aislamiento por workspace evita lecturas cruzadas.
- Las validaciones de nombre, idioma, audiencia, objetivo y tipo fallan en cerrado.
- El proyecto y el evento Outbox se confirman en una única transacción.
- Los reintentos no duplican `editorial.project.created`.
- El reinicio conserva proyecto, configuración inicial y entrega pendiente.
- No se realizan mutaciones remotas dentro de la transacción.

Riesgo residual: las futuras ediciones del proyecto deberán usar control de versión optimista y conservar historial de cambios.
