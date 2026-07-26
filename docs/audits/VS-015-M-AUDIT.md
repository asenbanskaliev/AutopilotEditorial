# VS-015 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- El alcance se limita al shell operativo: navegación, estado y preferencias locales.
- Las rutas Overview, System, Configuration y About están definidas y soportan deep links.
- La UI consume exclusivamente las APIs versionadas de diagnostics y configuración.
- Los flujos editoriales, autenticación y framework frontend permanecen fuera de alcance.

## M2 — Implementation

- Los assets son locales y se sirven desde `wwwroot` sin CDN, fuentes remotas ni scripts inline.
- El shell utiliza HTML semántico, CSS responsive y JavaScript ES modules sin paquetes externos.
- History API mantiene navegación, back/forward y reload por ruta.
- El último estado satisfactorio se conserva cuando la API queda offline.
- Tema y frecuencia de refresco se guardan solo en `localStorage`.
- El servidor expone una configuración saneada: versión API, ámbito de bind, flag remoto y valores UI soportados.
- El fallback se limita a cuatro rutas explícitas; `/api/*`, `/health/*` y rutas desconocidas continúan devolviendo Problem Details.
- `wwwroot` se resuelve desde ejecución en repositorio, `dotnet run` o salida publicada.

## M3 — Tests

Las pruebas estáticas verifican:

- existencia de HTML, CSS y JavaScript;
- estructura semántica y accesibilidad;
- ausencia de recursos externos y scripts inline;
- consumo de APIs versionadas;
- localStorage, History API y `aria-current`;
- focus visible, responsive y reduced-motion;
- contrato CI independiente.

El journey Kestrel real verifica:

- shell en `/`, `/system`, `/configuration` y `/about`;
- documento HTML `no-store`;
- CSS y JavaScript locales con tipos MIME correctos y caché de una hora;
- CSP, nosniff, no-referrer, DENY y Permissions-Policy;
- configuración pública saneada y valores permitidos;
- Problem Details para API, health y rutas desconocidas;
- shell disponible con readiness no sana;
- continuidad de liveness, readiness, diagnostics y correlation ID.

Todos los journeys previos de arquitectura, SQLite, Artifact Store y Outbox continúan en PASS.

## M4 — Security and Operations

- CSP restringe scripts, estilos, fuentes, conexiones e imágenes al origen local; bloquea objetos y framing.
- No existen URLs externas en el documento del shell.
- El endpoint de configuración no expone URL, workspace, conexión, variables ni secretos.
- El shell no interpreta HTML recibido de APIs; crea elementos DOM y asigna `textContent`.
- Las rutas del shell son una allowlist exacta, no un fallback global.
- Los assets activan CI cuando cambian HTML, CSS o JavaScript.
- El documento evita caché; assets estáticos usan caché corta y controlada.

Riesgo residual: las preferencias se almacenan en localStorage sin sincronización entre navegadores. Es intencional para esta fase local y no contiene datos sensibles.

## M5 — Product Flow

```text
abrir Control Center
→ cargar assets locales
→ resolver ruta History API
→ consultar diagnostics/configuration
→ representar ready/notReady/offline
→ navegar entre secciones
→ guardar preferencias locales
→ refrescar o recargar deep link
```

## Meta-Audit

- El primer GREEN falló porque se usó una propiedad tipada inexistente para `Referrer-Policy`; se corrigió con nombres de cabecera explícitos.
- El segundo intento falló porque `WebRoot` se cambiaba después de crear el builder; se trasladó a `WebApplicationOptions`.
- Ninguna expectativa HTTP o de seguridad se retiró durante las correcciones.
- No se añadió framework frontend para ocultar problemas de entrega.
- La preimplementación Outbox continúa registrada sin alterar el crédito de VS-015.
- No hay componentes productivos huérfanos.

## Evidencia

- RED Governance: run `30214543758`, job `89826287193`.
- Fallo de compilación inicial: run `30214858405`, job `89827184237`.
- Fallo de WebRoot: run `30214936175`, job `89827424676`.
- GREEN final .NET: run `30215013788`, job `89827647257`.
- GREEN final Governance: run `30215013808`.
- GREEN final Plan Integrity: run `30215013833`.
- Artifact: `8635556597`.
- Digest: `sha256:91055d8a47c2409cbab22ef823b2e96f7102b91dd2019826a4556c6ea3f053da`.
