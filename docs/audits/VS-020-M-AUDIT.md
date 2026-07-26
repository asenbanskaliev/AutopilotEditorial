# VS-020 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- El contrato usa JSON-RPC 2.0 y el lifecycle MCP oficial.
- La versión estable preferida es `2025-11-25`.
- Se declaran explícitamente las revisiones estables `2025-11-25`, `2025-06-18`, `2025-03-26` y `2024-11-05`.
- La negociación devuelve la revisión solicitada cuando está soportada y la última estable en otro caso.
- El transporte stdio exige UTF-8, un objeto por línea y stdout exclusivo para protocolo.
- Las capabilities del servidor son deliberadamente `{}`; no se anticipan tools, resources, prompts, logging, completions o tasks.

## M2 — Implementation

- `BookStudio.Mcp.Protocol` contiene versiones, envelopes, errores, modelos initialize y state machine.
- `BookStudio.Mcp.Transport` contiene únicamente framing y operaciones stdin/stdout/stderr.
- `Program.cs` es un composition root sin banner, lógica editorial ni acceso durable.
- Estados válidos:

```text
Created → InitializeResponded → Ready → Closed
```

- `ping` funciona sin alterar estado.
- `notifications/initialized` no genera respuesta y activa el estado Ready.
- Los IDs de request aceptan string o entero, nunca null, boolean, object, array o decimal.
- Los IDs se conservan en las respuestas y no pueden reutilizarse dentro de la sesión.
- El response initialize incluye versión negociada, capabilities vacías, identidad estable e instrucciones acotadas.
- Los errores estándar JSON-RPC se centralizan y el estado no inicializado usa `-32002`.

## M3 — Tests

Las pruebas estáticas verifican:

- archivos de protocolo, transport y journey;
- catálogo completo de revisiones estables;
- límite de 1 MiB;
- uso de `ReadLineAsync`/`WriteLineAsync`;
- ausencia de `Console.WriteLine` en el transporte y composition root;
- contrato CI `dotnet.mcp-initialize-integration`.

El journey de subprocess real verifica:

- parse error para JSON malformado;
- rechazo de batch;
- ping antes de initialize;
- rechazo de requests no permitidos antes de initialize;
- invalid params;
- negociación de la versión actual;
- echo de una versión legacy soportada;
- fallback a la última versión ante una revisión desconocida;
- capabilities vacías e identidad del servidor;
- rechazo de initialize duplicado;
- transición mediante `notifications/initialized` sin respuesta adicional;
- ping en Ready;
- method-not-found en Ready;
- notificaciones desconocidas sin respuesta;
- ID inválido no reflejado;
- mensaje superior a 1 MiB;
- EOF, exit code 0 y stdout restante vacío;
- ausencia de secretos o payloads en stderr.

Todos los journeys previos continúan en PASS.

## M4 — Security and Operations

- stdout contiene exclusivamente JSON-RPC compacto delimitado por newline.
- stderr recibe códigos saneados de hasta 96 caracteres; no recibe payloads ni excepciones.
- El parser limita profundidad a 64 y rechaza comments/trailing commas.
- La línea stdio se limita a 1 MiB en UTF-8.
- Method, IDs, versión e implementation fields tienen límites explícitos.
- Los errores internos no incluyen detalles técnicos.
- La sesión no accede a filesystem, SQLite, artifact store, red ni servicios editoriales.
- No se registra contenido de params, client capabilities ni client metadata.
- EOF y cancelación cierran la sesión sin protocolo inventado.

Riesgo residual: `ReadLineAsync` materializa la línea antes de comprobar el tamaño UTF-8. El límite evita procesamiento y persistencia de mensajes grandes, pero un transporte streaming con límite previo a materialización corresponde a la futura slice de security sandbox/conformance.

## M5 — Product Flow

```text
client launches BookStudio.Mcp
→ JSON-RPC ping or initialize
→ negotiate protocol version
→ initialize response with empty capabilities
→ notifications/initialized
→ Ready
→ requests/notifications
→ client closes stdin
→ server exits 0
```

## Meta-Audit

- El primer build falló únicamente porque las funciones locales top-level no admiten sobrecarga por tipo.
- Se renombraron las aserciones string/numeric sin reducir casos ni expectativas.
- El test inicia el assembly real con `dotnet BookStudio.Mcp.dll`.
- No existen sleeps, mocks de transporte o invocación directa como evidencia única.
- El banner previo se eliminó en lugar de filtrarse desde el test.
- No se adoptó un SDK externo para ocultar el lifecycle mínimo.
- No hay componentes productivos huérfanos.

## Evidencia

- RED Governance: run `30216888322`, job `89832550025`.
- Build RED del harness: run `30217294317`, job `89833606770`.
- GREEN .NET: run `30217406880`, job `89833894990`.
- GREEN Governance: run `30217406850`.
- GREEN Plan Integrity: run `30217406857`.
- Artifact: `8636209089`.
- Digest: `sha256:866b891c4f814272dd4e6636cd2ef84e081830c25e8535782475e033f10e5d97`.
