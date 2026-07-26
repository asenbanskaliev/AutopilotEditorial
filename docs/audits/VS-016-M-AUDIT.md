# VS-016 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- El contrato cubre de forma explícita trazas, métricas y logs mediante OpenTelemetry.
- La identidad de servicio usa valores estables y de baja cardinalidad.
- El snapshot local, la redacción, los límites, el muestreo y OTLP opcional están definidos.
- La exportación remota permanece desactivada por defecto y no es requisito para operación local.
- Los cuerpos, prompts, rutas, credenciales, mensajes de excepción y stacks están fuera del contrato exportable.

## M2 — Implementation

- Application define `BookStudioTelemetry` y los contratos provider-neutral de snapshot.
- Infrastructure implementa buffers acotados y exporters específicos de actividad, métrica y log.
- ControlCenter compone el SDK OpenTelemetry 1.17.0 con ASP.NET Core, Runtime y el meter propio.
- `ActivitySource` y `Meter` usan el nombre estable `BookStudio`.
- Los nombres de operación se validan como tokens ASCII de baja cardinalidad.
- El exporter de trazas conserva únicamente tags allowlisted.
- El exporter de logs guarda plantilla, categoría, nivel, tipo de excepción y atributos seguros; no guarda mensaje formateado ni stack.
- El exporter de métricas conserva descriptores, no puntos de alta cardinalidad.
- El endpoint `/api/v1/observability` devuelve el snapshot más reciente con límite de 1 a 100.
- La configuración pública informa flags y capacidad, pero nunca expone el endpoint OTLP.

## M3 — Tests

Las pruebas estáticas verifican:

- existencia de contratos, store, exporters, options y composition root;
- paquetes OpenTelemetry centralizados en 1.17.0;
- referencias sin versiones inline;
- política mínima de redacción;
- endpoint de observabilidad;
- contrato CI independiente.

El journey real verifica:

- validación de capacidad y sampling;
- rechazo de OTLP HTTP remoto, credenciales y query strings;
- aceptación de HTTPS y loopback HTTP;
- modo desactivado sin señales;
- buffers newest-first con overflow y dropped counts;
- redacción de claves sensibles y atributos no allowlisted;
- host Kestrel real;
- generación de custom spans y métricas BookStudio;
- logs estructurados y excepción representada solo por tipo;
- propagación W3C `traceparent`;
- `TracerProvider.ForceFlush` y `MeterProvider.ForceFlush`;
- snapshot acotado de las tres señales;
- ausencia de secretos, paths y mensajes de excepción en JSON;
- Problem Details para límites inválidos;
- exclusión del propio endpoint de observabilidad para evitar ruido recursivo.

Todos los journeys previos de arquitectura, SQLite, Artifact Store, Outbox y API/shell continúan en PASS.

## M4 — Security and Operations

- El store aplica capacidad independiente de 16 a 2.048 registros por señal.
- Los registros más antiguos se descartan y el número descartado queda visible.
- La allowlist impide exportar atributos arbitrarios o de alta cardinalidad.
- Las claves sensibles se eliminan antes de entrar al snapshot.
- Los valores se acotan y se eliminan caracteres de control.
- Los mensajes de excepción y stacks no se almacenan.
- La instrumentación ASP.NET solo acepta una allowlist de rutas conocidas.
- OTLP no admite credenciales, query strings ni fragments.
- HTTP solo se admite para collectors loopback; destinos remotos requieren HTTPS.
- Un fallo de exportación OTLP no forma parte de liveness ni de readiness.
- La evidencia local no requiere collector ni acceso de red externo.

Riesgo residual: el snapshot vive en memoria del proceso y se pierde al reiniciar. Es intencional para la línea base local; la retención durable o un collector administrado requieren una slice posterior con política de privacidad y almacenamiento propia.

## M5 — Product Flow

```text
request / operation / structured log
→ OpenTelemetry SDK
→ processors and metric reader
→ sanitized bounded exporters
→ in-memory snapshot
→ GET /api/v1/observability
```

Con OTLP habilitado:

```text
same SDK pipeline
→ local sanitized snapshot
+ validated OTLP exporter
→ external collector outside application transactions
```

## Meta-Audit

- El primer build falló por omitir el namespace raíz `OpenTelemetry` de los procesadores simples.
- La corrección fue exclusivamente de import; no se retiró instrumentación ni ninguna expectativa.
- El journey usa providers reales y `ForceFlush`, no mocks ni sleeps.
- El test de redacción introduce valores sintéticos sensibles y exige su ausencia completa.
- El endpoint de observabilidad está excluido del filtro de trazas para evitar autorreferencia.
- No se añadió dashboard, SaaS o collector prematuro.
- No hay componentes productivos huérfanos.

## Evidencia

- RED Governance: run `30215414344`, job `89828657642`.
- Build RED de API OpenTelemetry: run `30215755276`, job `89829552466`.
- GREEN funcional .NET: run `30216432300`, job `89831350990`.
- GREEN final .NET: run `30216552914`, job `89831668643`.
- GREEN final Governance: run `30216552925`, job `89831668749`.
- GREEN final Plan Integrity: run `30216552915`.
- Artifact final: `8635973316`.
- Digest final: `sha256:257354287caa12a26ae7b5787e3997efcc3252e58e213937317d1c3d4372663a`.
