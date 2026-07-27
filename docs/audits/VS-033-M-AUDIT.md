# VS-033 — Auditoría M

## Resultado

`PASS_PENDING_FINAL_HEAD`

## M1 — Specification

- Los perfiles son provider-neutral y pertenecen a Application.
- La resolución exige coincidencia exacta de `profileId`, versión, workflow y rol.
- Ausencia en allowlist significa denegación.
- Deny explícito prevalece siempre sobre allow.
- No existen grants por wildcard, prefijo o regex.
- Un perfil hijo solo puede estrechar capacidades, tools, aprobación y límites del padre.
- La compatibilidad del proveedor no puede ampliar permisos.
- Los catálogos publicados son versionados, bounded y controlados por repositorio.
- La huella SHA-256 identifica el perfil efectivo para auditoría; no se trata como firma ni credencial.
- La slice no crea sesiones, no envía prompts y no realiza mutaciones remotas.

## M2 — Implementation

### Application contracts

`AgentToolProfileContracts.cs` define:

- catálogo cerrado de capacidades conocidas;
- catálogo cerrado de tools MCP activas;
- códigos estables de rechazo;
- definición versionada;
- petición de resolución;
- límites centrales;
- perfil efectivo immutable por exposición;
- igualdad de valor y huella verificable.

La construcción de `EffectiveAgentToolProfile` es interna. Un consumidor externo no puede fabricar una autorización efectiva y presentarla al mapper como resultado legítimo del resolver.

### Catalog

`AgentToolProfileCatalog`:

- copia y ordena todas las definiciones;
- limita perfiles y entradas por lista;
- valida identificadores ASCII exactos y bounded;
- rechaza duplicados de `profileId + version`;
- rechaza tools/capabilities desconocidos;
- mantiene lookup inmutable después de construcción;
- no usa caches acumulativos ni estado mutable compartido.

### Resolver

`AgentToolProfileResolver`:

1. valida y canonicaliza la petición;
2. distingue profile inexistente de versión inexistente;
3. exige workflow y rol exactos;
4. aplica deny antes de allow;
5. exige allow explícito para cada valor solicitado;
6. verifica la huella del padre y la versión del catálogo;
7. exige subset exacto de capacidades y tools;
8. hace monotónica la aprobación humana;
9. limita llamadas/paralelismo mediante `Math.Min` con producto y padre;
10. genera SHA-256 determinista sin reloj, GUID ni aleatoriedad.

### Repository loader

`OpenCodeAgentToolProfileCatalogLoader`:

- acepta bytes bounded, no paths arbitrarios;
- usa JSON estricto, profundidad máxima y sin comentarios/trailing commas;
- rechaza propiedades duplicadas o desconocidas;
- limita perfiles y arrays antes de materializarlos;
- delega la semántica final al catálogo Application.

### Provider mapping

`OpenCodeAgentToolProfileMapper`:

- es puro y no contiene HTTP ni mutaciones;
- exige perfil efectivo con huella válida;
- exige soporte provider de deny-by-default y deny explícito;
- exige que toda tool permitida esté soportada;
- produce allow exacto y deny para el resto del inventario soportado;
- rechaza cualquier representación incompleta o expansiva;
- construye internamente el perfil mapeado.

## M3 — TDD Dual

### RED-I

Governance falló tras introducir la Spec y el contrato estático antes del código de producción. Faltaban contratos, resolver, loader, mapper, arquitectura y CI.

### RED-E

La solución registró primero el proyecto contractual; la compilación falló porque la API exigida todavía no existía.

### GREEN

El journey real ejecuta 12 escenarios:

1. carga del catálogo repository-controlled;
2. resolución workflow/rol;
3. deny-by-default;
4. deny-overrides-allow;
5. rechazo de valores desconocidos;
6. huella e igualdad deterministas;
7. selectores/versiones exactos;
8. narrowing de perfiles hijos;
9. aprobación y límites monotónicos;
10. mapping provider fail-closed;
11. resolución concurrente y cancelación;
12. ausencia de mutación y evidencia segura.

Resultado verificado:

```text
OPENCODE_AGENT_TOOL_PROFILES_PASS scenarios=12 profiles=5 fingerprints=6 gate=NO_PRIVILEGE_ESCALATION mutation=NONE
```

## M4 — Security and Operations

- La resolución no accede a red, procesos, shell ni provider.
- La carga runtime acepta un payload explícito; no enumera directorios.
- Los errores exponen solo códigos estables.
- Un valor sensible rechazado no aparece en mensajes ni huellas.
- El catálogo, listas, payload JSON, profundidad y mapping provider están bounded.
- El resolver es stateless y seguro para concurrencia.
- La cancelación se comprueba antes y durante la resolución.
- El padre debe tener huella válida y misma versión de catálogo.
- Los constructores efectivos/mapeados son internos; la huella no sustituye una frontera de autorización.
- No quedan workflows write-enabled ni scripts temporales.

Riesgos residuales aceptados:

- La huella prueba igualdad/integridad lógica, no autenticidad entre procesos.
- El catálogo se recarga por despliegue; esta slice no incorpora hot reload ni UI administrativa.
- El mapper cubre tools; futuras capacidades provider no representadas deberán seguir fallando cerrado.
- Los perfiles son process-local y no constituyen aislamiento de sistema operativo.
- La autorización final del endpoint que lance ejecuciones pertenece a slices posteriores.

## M5 — Product Flow

```text
repository profile catalog
→ strict bounded loader
→ immutable Application catalog
→ exact profile/version/workflow/role request
→ unknown-value rejection
→ deny-overrides-allow
→ explicit allow verification
→ optional parent subset verification
→ approval/limit narrowing
→ deterministic effective fingerprint
→ fail-closed provider mapping
→ immutable allow/deny result
```

## TestChangeRequest 033-001

Durante GREEN se detectaron dos incoherencias de ownership estático y semántica de valor:

- `AGENTS.md` se alineó con los encabezados canónicos `Allowed/Forbidden`.
- El marcador literal `mutation=NONE` se situó también en el owner del journey.
- `EffectiveAgentToolProfile` añadió operadores de igualdad coherentes con su igualdad estructural ya especificada.

No se eliminó ni relajó ningún escenario.

## Meta-Audit

- La Spec fue restaurada sobre `main` remediado `93cc967730aff406419ef76fe63fe7396a5872c9`.
- Governance fue escrito antes de la implementación.
- RED-I y RED-E quedaron observables en GitHub Actions.
- El journey usa catálogo, resolver, loader y mapper reales; no mockea la política.
- La revisión de confianza cerró los constructores antes del gate final.
- Solución, arquitectura, catálogo CI y workflow están enlazados.
- El workflow permanente conserva `contents: read`.
- Todos los journeys acumulativos permanecieron en PASS en el GREEN previo a auditoría.
- El head final auditado debe volver a ejecutar Plan Integrity, Governance y `.NET CI` antes del merge.

## Evidencia GREEN previa a auditoría

- Head: `aa48d3dd3f2762a01c8efc8cdbc55cec8885742f`.
- Plan Integrity: run `30306587107` — PASS.
- Governance: run `30306587103` — PASS.
- Governance artifact: `8668657340`.
- Governance digest: `sha256:274bd6f68da57ffa5b9e1c954820584f48e459a72f1fb7191d912273876d230f`.
- .NET CI: run `30306587101`, job `90112209197` — PASS.
- .NET artifact: `8668701240`.
- .NET digest: `sha256:60bfe79d726b89f4b561975d847341d084bd05d1af06e3230429cf0b936ba475`.
- Contract result: PASS, exit code 0.
- stdout SHA-256: `28c2f7fef495a4581242f9ea513d53a4ddf03ec19a041fb942324ddf6a183659`.
- stderr SHA-256: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
- retry chain: empty.
