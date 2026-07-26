# VS-015 — RetroSpec

## Implemented contract

El Control Center dispone de un shell web local, accesible y responsive servido por el mismo host ASP.NET que la API v1.

## Delivery contract

- Assets canónicos: `wwwroot/index.html`, `wwwroot/app.css`, `wwwroot/app.js`.
- No existen dependencias, CDN, fuentes externas, scripts inline ni proceso de build frontend.
- Rutas de documento: `/`, `/system`, `/configuration`, `/about`.
- El documento usa `Cache-Control: no-store`.
- CSS y JavaScript usan caché pública de una hora.
- Cualquier ruta no incluida en la allowlist conserva el comportamiento Problem Details.

## Navigation contract

- Navigation links use History API and `aria-current`.
- Browser back/forward updates the active section.
- Direct reload of every declared route returns the shell.
- The main region receives focus after in-app navigation.
- Page titles change per section.

## Operational state contract

The shell consumes:

- `GET /api/v1/diagnostics`;
- `GET /api/v1/configuration`.

Visual states:

- `loading` before first response;
- `ready` when every readiness check passes;
- `notReady` when the host is live but a dependency is unhealthy;
- `offline` when the API cannot be reached.

A request failure preserves the last successful diagnostics and marks them as stale rather than clearing the screen.

## Preferences contract

Browser-local values:

- theme: `system`, `light`, `dark`;
- automatic refresh: `0`, `5`, `15`, `30`, `60` seconds.

Values are validated before use and stored in namespaced localStorage keys. Manual refresh remains available.

## Server configuration contract

`GET /api/v1/configuration` returns only:

- `apiVersion`;
- `bindScope`;
- `remoteBindingEnabled`;
- supported themes;
- supported refresh intervals.

It never returns workspace paths, URLs, connection data, environment variables or secrets.

## Security headers

Every response passes through:

- Content-Security-Policy restricted to self;
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: no-referrer`;
- `X-Frame-Options: DENY`;
- restrictive Permissions-Policy.

The shell dynamically renders API data with DOM nodes and `textContent`, not injected HTML.

## Accessibility contract

- semantic header, nav, main and footer;
- skip link;
- keyboard-operable links, buttons and selects;
- visible `:focus-visible`;
- `aria-current` and live regions;
- textual status independent of color;
- responsive single-column mode;
- reduced-motion CSS support.

## Deployment contract

The composition root resolves shell assets from:

1. current content root `wwwroot`;
2. application base directory `wwwroot`;
3. repository layout `src/BookStudio.ControlCenter/wwwroot`.

WebRoot is supplied through `WebApplicationOptions` before builder creation.

## Follow-on constraints

- Future UI slices must preserve the exact API/health fallback boundary.
- A frontend framework may replace the asset implementation only through a tested migration that retains local delivery, CSP and deep links.
- Editorial screens must consume versioned APIs and must not access SQLite, blobs or workspace paths directly.
- Remote binding remains disabled until authentication, authorization and TLS are implemented.
- Any new user preference must be classified as local-only or durable before implementation.

## Next slice

`VS-016 — Observability`.
