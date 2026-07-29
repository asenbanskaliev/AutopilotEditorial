# VS-067 RetroSpec

## What changed

El sistema puede proponer, validar y aplicar reparaciones narrativas localizadas sin reescrituras amplias ni pérdida de procedencia.

## Learned constraints

- La autoridad debe justificar exactamente la reparación.
- Scope, versión y digest forman parte de la precondición material.
- Las operaciones deben ser tipadas y localizadas.
- El drift convierte el patch en `STALE` sin mutación parcial.
- El historial anterior es append-only y recuperable.
- Replay requiere comparar el payload real además del fingerprint declarado.
- Target, patch, historial, recibo y Outbox se confirman en la misma transacción.

## Follow-through

`VS-068 Chapter gate lock` debe consumir el resultado durable de los repair patches y bloquear capítulos solo cuando no existan findings bloqueantes ni patches pendientes, stale o rechazados sin resolución.