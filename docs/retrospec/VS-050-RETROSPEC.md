# VS-050 RetroSpec — Project journey

## Entregado

Recorrido vertical completo para crear un proyecto editorial con identidad durable, configuración inicial, aislamiento por workspace, validaciones fail-closed y evento Outbox transaccional.

## Reglas durables

- El request ID y su fingerprint definen la idempotencia de creación.
- La identidad del proyecto permanece estable después del reinicio.
- Un workspace solo puede leer sus propios proyectos.
- El evento de creación comparte transacción con el proyecto.
- Las repeticiones idénticas no duplican eventos.
- Los conflictos de contenido inmutable se rechazan.

Status: VERIFIED.
