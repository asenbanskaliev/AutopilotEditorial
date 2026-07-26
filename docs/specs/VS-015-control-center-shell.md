# VS-015 — Control Center Shell

## IntentSpec

### Problem

The verified API is operational but inaccessible to a normal local user without manually calling endpoints. The product needs a stable, accessible shell before adding editorial journeys.

### Objective

Serve a dependency-free local web shell with navigation, current system state and safe UI configuration, using only the versioned Control Center API.

## BehaviorSpec

### Delivery

- Assets are local under `wwwroot`; no CDN, inline script or external font.
- `index.html`, `app.css` and `app.js` are immutable source assets.
- Static assets may be cached; the shell document uses no-store.
- `/`, `/system`, `/configuration` and `/about` return the same shell for History API deep links.
- Unknown `/api/*` and `/health/*` routes remain Problem Details and are never replaced by HTML.

### Navigation

- Overview: readiness summary and last refresh.
- System: service, version, environment, uptime and dependency checks.
- Configuration: local theme and refresh interval plus safe server configuration summary.
- About: product identity, local-only baseline and current program status.
- Browser back/forward and direct reload preserve the selected section.

### State

- Fetch `/api/v1/diagnostics` and `/api/v1/configuration`.
- Distinguish `ready`, `notReady`, `loading` and `offline`.
- Automatic refresh is configurable to 0, 5, 15, 30 or 60 seconds.
- A manual refresh remains available.
- Failed requests do not erase the last successful state.

### UI configuration

- Theme values: `system`, `light`, `dark`.
- Refresh interval is stored in localStorage.
- Server configuration endpoint exposes only API version, bind scope, remote-binding flag and supported UI values.
- No workspace path, URL, environment variable or secret is returned.

### Accessibility

- Semantic header/nav/main/footer.
- Skip link.
- Keyboard-operable navigation and controls.
- Visible focus.
- `aria-current`, `aria-live` and status text independent of color.
- Responsive layout and reduced-motion support.

### Security

Every shell response includes:

- Content-Security-Policy restricted to self;
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: no-referrer`;
- `X-Frame-Options: DENY`;
- no external asset references.

## TDD Dual

- RED-I: assets, shell endpoint, configuration endpoint and CI contract do not exist.
- RED-E: Kestrel cannot prove root/deep-link delivery, assets, headers or fallback separation.
- GREEN-I: static governance and architecture pass.
- GREEN-E: real HTTP journey proves shell, deep links, APIs, headers and safe fallback.

## Audit M

- M1 navigation/state/configuration contract.
- M2 presentation-only implementation with no editorial logic.
- M3 static, healthy, unready and deep-link tests.
- M4 CSP, no external assets, sanitized config and API fallback.
- M5 open → navigate → status → configure → reload journey.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.
