# VS-084 RetroSpec

## Resultado

Sincronizada.

## Contrato confirmado tras implementación

- una revisión de diálogo solo puede derivar de una revisión voice/line aprobada, exacta y vigente;
- el nodo `DIALOGUE` debe estar `READY` o `IN_PROGRESS` en el plan editorial;
- la evaluación materializa findings trazables por categoría, severidad, evidencia y localización fina;
- la aprobación falla de forma cerrada mientras existan findings bloqueantes abiertos;
- retorno a reparación conserva revisión esperada, razón, atribución e historial;
- reapertura y detección de drift invalidan decisiones previas sin borrar evidencia;
- cada comando admite replay idempotente únicamente cuando identidad y payload coinciden;
- persistencia, historial y Outbox forman una única frontera transaccional;
- reinicio y aislamiento por workspace son requisitos de aceptación.

## Aprendizaje incorporado

La localización por capítulo, escena, intercambio, hablante, línea y span forma parte del contrato editorial y no puede reducirse a texto libre sin estructura.

## Impacto posterior

`VS-085` debe consumir únicamente una decisión de diálogo aprobada y vigente, y tratar reaperturas o drift de `VS-084` como invalidación fail-closed de su autoridad.
