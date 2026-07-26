# BookStudio / AutopilotEditorial — Master Plan v0.11.0

## 1. Objetivo

Construir un producto completo para producir libros profesionales mediante IA, OpenCode y MCP, desde la idea inicial hasta una edición EPUB/PDF/DOCX validada, con preparación KDP, trazabilidad, seguridad, accesibilidad, operación local y perfil Enterprise.

El programa no termina en un MVP. Las entregas intermedias son checkpoints y el único cierre permitido es `FULL_PROGRAM_COMPLETE`.

## 2. Arquitectura

```text
Control Center
  ↓
Application + Domain
  ↓ transacción
SQLite/PostgreSQL + Transactional Outbox + Audit Log
  ↓
Durable Workflow Engine
  ↓
Scheduler + Worker
  ↓
OpenCode Server / modelos
  ↓ tools acotadas
MCP bounded servers
  ↓
Application use cases
  ↓
Artifacts, memory, gates, render and release
```

### Componentes

- `BookStudio.Domain`: invariantes y estados canónicos.
- `BookStudio.Application`: casos de uso y contratos.
- `BookStudio.Infrastructure`: SQLite, PostgreSQL, filesystem, Outbox y adapters.
- `BookStudio.Mcp`: protocolo y exposición de tools/resources/prompts sin lógica de negocio.
- `BookStudio.OpenCode`: sesiones, prompts, eventos, cancelación y compatibilidad.
- `BookStudio.Autopilot`: workflows, next-step resolver y human gates.
- `BookStudio.Worker`: jobs, lease, heartbeat, timeout, retry y dead letter.
- `BookStudio.ControlCenter`: onboarding, progreso, decisiones, configuración y outputs.
- `BookStudio.Render`: EPUB, PDF, DOCX, imágenes y preflight.

## 3. Superficie MCP pública

Cinco servidores acotados:

1. `book-core`
2. `book-authoring`
3. `book-quality`
4. `book-production`
5. `book-ops`

La superficie pública se limita a 29 tools de alto nivel. Las funciones editoriales finas son capacidades internas del Core y no saturan el contexto del modelo.

### Contrato MCP

- baseline estable MCP `2025-11-25`;
- negociación de versión;
- `inputSchema` y `outputSchema`;
- `structuredContent`;
- annotations de lectura, destrucción e idempotencia;
- paginación determinista;
- cancelación y progreso;
- errores protocolarios separados de errores de dominio;
- extensiones draft solo con feature flags y fallback.

## 4. Estado durable

La única fuente durable es:

```text
AutopilotWorkflowRun + AutopilotJob
```

No son canónicos:

- sesiones OpenCode;
- SSE;
- conversación del modelo;
- archivos temporales;
- MCP Tasks.

Se utiliza estado transaccional, Outbox, audit log append-only, snapshots y replay. No se implementa Event Sourcing completo.

## 5. Gestión de contexto y memoria

Cada tarea recibe un `ContextManifest` con:

- versiones;
- hashes;
- procedencia;
- trust level;
- frescura;
- tokens estimados;
- omisiones;
- conflictos;
- reserva de salida.

La memoria se divide en canónica y derivada. Resúmenes, embeddings e índices nunca modifican directamente la memoria canónica.

## 6. Seguridad

- contenido externo tratado como datos, no instrucciones;
- defensa contra prompt injection;
- deny-by-default;
- sandbox de workspace;
- normalización de rutas y bloqueo de symlinks externos;
- secretos fuera del repositorio;
- allowlist de procesos;
- egress policy y modo local-only;
- SBOM, lockfiles, hashes y dependency scanning;
- trazas y diagnósticos redactados.

## 7. Metodología de desarrollo

Cada vertical slice aplica `SDD-DTDD-M`:

```text
IntentSpec
→ BehaviorSpec
→ Spec Challenge
→ SPEC_READY
→ RED-I + RED-E
→ DUAL_RED_CONFIRMED
→ implementación
→ GREEN-I + GREEN-E
→ DUAL_GREEN
→ Auditorías M
→ Meta-audit
→ RetroSpec
→ PR
```

### TDD Dual

- TDD interno: dominio, aplicación, contratos, persistencia, arquitectura y migrations.
- TDD externo: UI, API, Outbox, Worker, OpenCode, MCP, recovery y resultado visible.

### Auditoría M

- M1 especificación;
- M2 implementación;
- M3 tests;
- M4 seguridad y operaciones;
- M5 flujo de producto;
- Meta-auditor independiente.

El implementador no puede aprobar su propio trabajo ni debilitar tests confirmados sin `TestChangeRequest`.

## 8. Autopiloto de desarrollo

```text
Leer GitHub
→ seleccionar slice READY
→ crear rama
→ ejecutar SDD-DTDD-M
→ abrir PR
→ resolver checks y reviews
→ merge gate
→ actualizar estado
→ seleccionar siguiente slice
```

GitHub conserva issues, ramas, PR, checks, decisiones, evidencias y el punto de reanudación.

## 9. Fases del programa

- F0 Bootstrap.
- F1 Foundation.
- F2 MCP.
- F3 OpenCode.
- F4 Autopilot.
- F5 Authoring.
- F6 Coherence.
- F7 Professional Editing.
- F8 Research and Rights.
- F9 Visual.
- F10 Production.
- F11 Operations.
- F12 Enterprise.
- F13 Certification.

El backlog ejecutable contiene 104 vertical slices en `full-program-backlog.csv`.

## 10. Flujo editorial completo

```text
Idea
→ discovery
→ propuesta
→ especificación
→ investigación
→ diseño
→ planificación
→ escenas
→ capítulos
→ coherencia
→ edición profesional
→ fact-check y derechos
→ imágenes
→ accesibilidad
→ maquetación
→ preflight
→ galeradas
→ release
→ paquete KDP
```

## 11. Producción profesional

El producto debe incluir:

- coherencia de párrafos, escenas, capítulos y manuscrito;
- estados de personajes, conocimiento, cronología y tramas;
- edición developmental, structural, content, line, copyedit y proofreading;
- voz, diálogo, pacing, temas, beta readers, originalidad y lectura en voz alta;
- claims, fuentes, citas, derechos, licencias y disclosure de IA;
- briefs visuales, asset registry, adapters de imagen, auditoría y portada;
- EPUBCheck, Ace, PDF preflight, fuentes, DPI, TOC, links y metadata;
- galeradas, prueba física y professional release gate.

## 12. Operabilidad

- installer y prerequisites;
- doctor y health checks;
- backup/restore/export;
- safe mode;
- update y rollback;
- support bundle;
- observabilidad OpenTelemetry;
- límites de CPU, RAM, almacenamiento, jobs y coste;
- perfil offline/local-only.

## 13. Enterprise

- PostgreSQL;
- autenticación y organizaciones;
- RBAC;
- colaboración;
- remote deployment;
- Remote MCP con OAuth;
- workflow engine distribuido;
- políticas y reporting.

## 14. CI y evidencia

El proveedor de validación es intercambiable:

- GitHub Actions;
- runner self-hosted;
- CircleCI;
- evidencia local reproducible.

Una validación no ejecutada nunca se convierte en PASS.

Cada slice genera:

- Spec;
- tests RED/GREEN;
- logs;
- traces;
- screenshots o artifacts;
- orphan scan;
- architecture fitness;
- security report;
- RetroSpec;
- completion report.

## 15. Gate final

`FULL_PROGRAM_COMPLETE` requiere:

- 104 slices completas o excluidas contractualmente;
- cero stubs/mocks productivos;
- cero TODO/FIXME P0/P1;
- todos los journeys E2E en PASS;
- MCP conformance;
- DUAL_GREEN;
- M_AUDIT_PASS;
- seguridad, accesibilidad, rendimiento, chaos, replay y regresión en PASS;
- migrations, backup/restore y rollback probados;
- trazabilidad 100 %;
- instaladores, SBOM, checksums, manifests y documentación final.

## 16. Estado inicial

- Rama de bootstrap: `agent/bootstrap-full-program`.
- Primer PR: `PR-000 Governance Bootstrap`.
- Slice inicial: `VS-000`.
- Siguiente slice tras bootstrap: `VS-001`.
