# VS-027 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- Suite transversal MCP 2025-11-25 separada de los journeys funcionales.
- Matriz obligatoria de cinco procesos reales:
  - `BookStudio.Mcp`;
  - `BookStudio.Mcp.Authoring`;
  - `BookStudio.Mcp.Quality`;
  - `BookStudio.Mcp.Production`;
  - `BookStudio.Mcp.Ops`.
- Corpus v1 versionado para malformed input y lifecycle.
- Generación determinista con seed `27027`, 128 casos por proceso y 640 casos totales.
- Contratos explícitos de no-crash, no-hang, recovery, no-leak, workspace perezoso y EOF.
- La suite no ejecuta tools productivas con efectos ni accede directamente a routers o servicios.

## M2 — Implementation

- `McpProcessDriver` usa `dotnet` y stdio real con timeouts de lectura y cierre.
- `McpConformanceRunner` carga y valida el corpus embebido, ejecuta lifecycle, corpus, generación determinista, profundidad, oversize y pings de supervivencia.
- Cada proceso valida identidad, protocolo y capabilities exactas `prompts/resources/tools`.
- Los casos generados cubren jsonrpc, method, id y params con códigos esperados calculados por categoría.
- Un `IncrementalHash` SHA-256 registra assembly y payload de cada caso generado en orden estable.
- `Program` emite una única línea de éxito tipada desde el informe final.
- El corpus y el runner están registrados en solución, arquitectura y CI.

## M3 — Tests

Los contratos estáticos verifican:

- archivos requeridos;
- corpus versionado, categorías, fases e IDs únicos;
- cinco assemblies reales;
- seed, número de casos, SHA-256 y límite de transporte;
- process driver con stdin/stdout/stderr y timeouts;
- ausencia de acceso a `McpSession` o `IMcpFeatureRouter`;
- solución, arquitectura, contrato CI y workflow.

El journey externo verifica por servidor:

- ping antes de initialize;
- corpus `created`;
- profundidad superior a 64;
- initialize notification sin respuesta y recuperación;
- initialize válido e identidad exacta;
- duplicate initialize;
- initialized y duplicate initialized notification;
- tools/list, resources/list y prompts/list;
- corpus `ready`;
- request ID reutilizado;
- notification desconocida con canario;
- 128 casos deterministas y ping cada 16;
- mensaje superior a 1 MiB y recuperación;
- ping final;
- workspace inexistente;
- stderr saneado;
- EOF, exit 0 y sin stdout pendiente.

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- Ninguna entrada del corpus supera 1 MiB; oversize se genera de forma controlada.
- Timeout de respuesta: 10 segundos.
- Timeout de cierre: 20 segundos.
- Una respuesta ausente, adicional o no JSON falla inmediatamente.
- Canary secreto no puede aparecer en stderr; stdout se consume exclusivamente como JSON-RPC.
- Workspace root, `.bookstudio`, `bookstudio.db` y connection strings están prohibidos en diagnostics.
- Solo se permiten códigos de stderr `[A-Za-z0-9_-]` de hasta 96 caracteres.
- No network, models, external fuzz service, nondeterministic seed o retries que oculten crashes.
- El corpus se valida antes de ejecutar: schema, versión, phases, IDs, codes, payload bounds y expected IDs.

Riesgos residuales:

- La suite cubre categorías deterministas, no todos los posibles bytes de entrada.
- El transporte actual es newline-delimited stdio; futuros transports requerirán suites independientes.
- La conformance MCP completa del ecosistema podrá exigir nuevos casos conforme evolucione la especificación.

## M5 — Product Flow

```text
load immutable corpus v1
→ launch bounded MCP process
→ created lifecycle corpus
→ initialize and ready
→ feature discovery
→ ready corpus
→ deterministic generated cases with survival pings
→ oversize recovery
→ no-leak/no-workspace checks
→ EOF
→ aggregate reproducible report
```

## TestChangeRequest

### TCR-027-001

Aprobó separar responsabilidades:

- runner: ejecución, corpus, casos generados, hash e informe;
- entrypoint: marcador `MCP_CONFORMANCE_PASS` y exit code.

No se redujo ninguna expectativa observable.

## Meta-Audit

- RED confirmado sobre head `58ba04df4307fcfee5a550888c8c4b367c041420`:
  - Plan Integrity `30261205393` PASS;
  - Governance `30261205336` FAIL esperado por componentes ausentes.
- Primer GREEN de implementación en head `3a2e7878509b5a2bf74ee1bb9c49a1a920639ea3` detectó dos errores de compilación reales en el driver; no se ejecutó el journey ni se ocultó el fallo.
- El repair sustituyó anonymous conditional types por ramas explícitas sin modificar el corpus.
- La suite completa pasó en el siguiente head funcional.
- No se eliminaron casos ni se rebajaron códigos esperados.
- No hay mocks, acceso directo a routers, procesos sustitutos o componentes huérfanos.

## Evidencia GREEN

- Head funcional: `779e86714e5641b92dacfd1985e5130b1c6411a2`.
- Plan Integrity: run `30262310946` PASS.
- Governance: run `30262310999` PASS.
- Governance artifact: `8651376125`.
- Governance digest: `sha256:b0bc32013ee25f48c125643b2af0d5b23e272b15d0de860420d72766ac06f5a4`.
- .NET CI: run `30262310959`, job `89964849538` PASS.
- .NET artifact: `8651404980`.
- .NET digest: `sha256:a51473caa5e4960d8a0f545ea1f060f4579816867b16530ad0235355acc0a735`.
- Conformance result: PASS, exit code 0, stderr vacío.
- Output:
  - servers: 5;
  - corpus: 27;
  - generated cases: 640;
  - seed: 27027;
  - generated stream SHA-256: `2af65427878c95b3d582413703247f46828debca5d694f0a456ef0a65b61d4b2`.
