const routes = new Set(["/", "/system", "/configuration", "/about"]);
const allowedThemes = new Set(["system", "light", "dark"]);
const allowedRefreshIntervals = new Set([0, 5, 15, 30, 60]);
const storageKeys = Object.freeze({
  theme: "bookstudio.controlCenter.theme",
  refreshInterval: "bookstudio.controlCenter.refreshIntervalSeconds",
});

const state = {
  diagnostics: null,
  configuration: null,
  lastSuccessfulRefresh: null,
  loading: false,
  offline: false,
  refreshTimer: null,
  preferences: loadPreferences(),
};

const elements = {
  navLinks: [...document.querySelectorAll(".nav-link")],
  routeViews: [...document.querySelectorAll(".route-view")],
  refreshButton: document.querySelector("#refresh-button"),
  globalStatusDot: document.querySelector("#global-status-dot"),
  globalStatusText: document.querySelector("#global-status-text"),
  overviewBadge: document.querySelector("#overview-badge"),
  metricService: document.querySelector("#metric-service"),
  metricVersion: document.querySelector("#metric-version"),
  metricReadiness: document.querySelector("#metric-readiness"),
  metricEnvironment: document.querySelector("#metric-environment"),
  metricUptime: document.querySelector("#metric-uptime"),
  metricRefresh: document.querySelector("#metric-refresh"),
  overviewChecks: document.querySelector("#overview-checks"),
  systemService: document.querySelector("#system-service"),
  systemVersion: document.querySelector("#system-version"),
  systemEnvironment: document.querySelector("#system-environment"),
  systemUptime: document.querySelector("#system-uptime"),
  configurationApiVersion: document.querySelector("#config-api-version"),
  configurationBindScope: document.querySelector("#config-bind-scope"),
  configurationRemoteBinding: document.querySelector("#config-remote-binding"),
  systemChecks: document.querySelector("#system-checks"),
  preferencesForm: document.querySelector("#preferences-form"),
  themeSelect: document.querySelector("#theme-select"),
  refreshSelect: document.querySelector("#refresh-select"),
  settingsMessage: document.querySelector("#settings-message"),
  configurationSecuritySummary: document.querySelector("#configuration-security-summary"),
  offlineBanner: document.querySelector("#offline-banner"),
  footerUpdated: document.querySelector("#footer-updated"),
};

initialize();

function initialize() {
  applyTheme(state.preferences.theme);
  elements.themeSelect.value = state.preferences.theme;
  elements.refreshSelect.value = String(state.preferences.refreshIntervalSeconds);

  for (const link of elements.navLinks) {
    link.addEventListener("click", handleNavigationClick);
  }
  window.addEventListener("popstate", () => renderRoute(window.location.pathname, false));
  elements.refreshButton.addEventListener("click", () => refreshData({ announce: true }));
  elements.preferencesForm.addEventListener("submit", savePreferences);

  renderRoute(window.location.pathname, false);
  configureAutoRefresh();
  void refreshData({ announce: false });
}

function handleNavigationClick(event) {
  const link = event.currentTarget;
  const route = link.dataset.route;
  if (!routes.has(route) || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
    return;
  }

  event.preventDefault();
  if (window.location.pathname !== route) {
    window.history.pushState({}, "", route);
  }
  renderRoute(route, true);
}

function renderRoute(requestedRoute, moveFocus) {
  const route = routes.has(requestedRoute) ? requestedRoute : "/";
  for (const link of elements.navLinks) {
    const current = link.dataset.route === route;
    if (current) {
      link.setAttribute("aria-current", "page");
    } else {
      link.removeAttribute("aria-current");
    }
  }

  for (const view of elements.routeViews) {
    view.hidden = view.dataset.view !== route;
  }

  const titleByRoute = {
    "/": "Resumen",
    "/system": "Sistema",
    "/configuration": "Configuración",
    "/about": "Acerca de",
  };
  document.title = `${titleByRoute[route]} — Autopilot Editorial`;

  if (moveFocus) {
    document.querySelector("#main-content")?.focus({ preventScroll: true });
    window.scrollTo({ top: 0, behavior: "smooth" });
  }
}

async function refreshData({ announce }) {
  if (state.loading) {
    return;
  }

  state.loading = true;
  setLoadingState();
  try {
    const [diagnosticsResponse, configurationResponse] = await Promise.all([
      fetch("/api/v1/diagnostics", {
        headers: { Accept: "application/json" },
        cache: "no-store",
      }),
      fetch("/api/v1/configuration", {
        headers: { Accept: "application/json" },
        cache: "no-store",
      }),
    ]);

    if (!diagnosticsResponse.ok || !configurationResponse.ok) {
      throw new Error("Control Center API returned an unsuccessful response.");
    }

    const [diagnostics, configuration] = await Promise.all([
      diagnosticsResponse.json(),
      configurationResponse.json(),
    ]);
    validateDiagnostics(diagnostics);
    validateConfiguration(configuration);

    state.diagnostics = diagnostics;
    state.configuration = configuration;
    state.lastSuccessfulRefresh = new Date();
    state.offline = false;
    renderOperationalState();
    if (announce) {
      announceSettings("Estado actualizado.");
    }
  } catch {
    state.offline = true;
    renderOperationalState();
    if (announce) {
      announceSettings("No se pudo actualizar el estado.");
    }
  } finally {
    state.loading = false;
  }
}

function validateDiagnostics(value) {
  if (!value || typeof value !== "object" || typeof value.status !== "string" || !Array.isArray(value.checks)) {
    throw new Error("Diagnostics response is invalid.");
  }
}

function validateConfiguration(value) {
  if (!value || typeof value !== "object" || typeof value.apiVersion !== "string" || typeof value.remoteBindingEnabled !== "boolean") {
    throw new Error("Configuration response is invalid.");
  }
}

function setLoadingState() {
  elements.refreshButton.disabled = true;
  elements.refreshButton.textContent = "Actualizando…";
  if (!state.diagnostics) {
    setGlobalStatus("loading", "Comprobando sistema…");
    setOverviewBadge("loading", "Cargando");
  }
}

function renderOperationalState() {
  elements.refreshButton.disabled = false;
  elements.refreshButton.textContent = "Actualizar estado";
  elements.offlineBanner.hidden = !state.offline;

  if (state.offline && !state.diagnostics) {
    setGlobalStatus("offline", "API local no disponible");
    setOverviewBadge("offline", "Sin conexión");
    elements.metricReadiness.textContent = "Sin conexión";
    elements.metricRefresh.textContent = "Sin datos disponibles";
    elements.footerUpdated.textContent = "No se pudo obtener el estado";
    return;
  }

  const diagnostics = state.diagnostics;
  if (!diagnostics) {
    return;
  }

  const ready = diagnostics.status === "ready";
  const statusKind = state.offline ? "offline" : ready ? "ready" : "warning";
  const statusText = state.offline
    ? "Estado anterior; API sin conexión"
    : ready
      ? "Sistema preparado"
      : "Sistema requiere atención";
  setGlobalStatus(statusKind, statusText);
  setOverviewBadge(statusKind, state.offline ? "Estado anterior" : ready ? "Preparado" : "No preparado");

  elements.metricService.textContent = diagnostics.service ?? "Servicio local";
  elements.metricVersion.textContent = `Versión ${diagnostics.version ?? "—"}`;
  elements.metricReadiness.textContent = ready ? "Preparado" : "No preparado";
  elements.metricEnvironment.textContent = `Entorno ${diagnostics.environment ?? "—"}`;
  elements.metricUptime.textContent = formatDuration(diagnostics.uptimeSeconds);
  elements.metricRefresh.textContent = formatRefreshTime(state.lastSuccessfulRefresh, state.offline);

  elements.systemService.textContent = diagnostics.service ?? "—";
  elements.systemVersion.textContent = diagnostics.version ?? "—";
  elements.systemEnvironment.textContent = diagnostics.environment ?? "—";
  elements.systemUptime.textContent = formatDuration(diagnostics.uptimeSeconds);

  renderChecks(elements.overviewChecks, diagnostics.checks);
  renderChecks(elements.systemChecks, diagnostics.checks);
  renderConfiguration();

  elements.footerUpdated.textContent = state.lastSuccessfulRefresh
    ? `Última actualización: ${formatDateTime(state.lastSuccessfulRefresh)}`
    : "Estado aún no actualizado";
}

function renderChecks(container, checks) {
  container.replaceChildren();
  if (!Array.isArray(checks) || checks.length === 0) {
    const empty = document.createElement("p");
    empty.className = "empty-state";
    empty.textContent = "No hay comprobaciones registradas.";
    container.append(empty);
    return;
  }

  for (const check of checks) {
    const item = document.createElement("div");
    item.className = "check-item";

    const identity = document.createElement("div");
    const name = document.createElement("strong");
    name.textContent = humanize(check.name ?? "dependency");
    const meta = document.createElement("div");
    meta.className = "check-meta";
    const migrationParts = [];
    if (Number.isInteger(check.appliedMigrationCount)) {
      migrationParts.push(`${check.appliedMigrationCount} migraciones`);
    }
    if (Number.isInteger(check.latestMigrationVersion)) {
      migrationParts.push(`última v${check.latestMigrationVersion}`);
    }
    meta.textContent = migrationParts.join(" · ") || "Comprobación local";
    identity.append(name, meta);

    const badge = document.createElement("span");
    const ready = Boolean(check.ready);
    badge.className = `badge ${ready ? "badge-ready" : "badge-warning"}`;
    badge.textContent = ready ? "Preparado" : humanize(check.status ?? "No preparado");

    item.append(identity, badge);
    container.append(item);
  }
}

function renderConfiguration() {
  const configuration = state.configuration;
  if (!configuration) {
    return;
  }

  elements.configurationApiVersion.textContent = configuration.apiVersion;
  elements.configurationBindScope.textContent = configuration.bindScope === "loopback" ? "Solo equipo local" : "Acceso remoto habilitado";
  elements.configurationRemoteBinding.textContent = configuration.remoteBindingEnabled ? "Habilitado" : "Deshabilitado";
  elements.configurationSecuritySummary.textContent = configuration.remoteBindingEnabled
    ? "La instancia admite escucha remota. Antes de exponerla, deben completarse autenticación, autorización y TLS."
    : "La instancia escucha únicamente en loopback. No acepta conexiones de otros equipos por defecto.";
}

function savePreferences(event) {
  event.preventDefault();
  const selectedTheme = elements.themeSelect.value;
  const selectedRefresh = Number.parseInt(elements.refreshSelect.value, 10);

  if (!allowedThemes.has(selectedTheme) || !allowedRefreshIntervals.has(selectedRefresh)) {
    announceSettings("Las preferencias seleccionadas no son válidas.");
    return;
  }

  state.preferences = {
    theme: selectedTheme,
    refreshIntervalSeconds: selectedRefresh,
  };
  localStorage.setItem(storageKeys.theme, selectedTheme);
  localStorage.setItem(storageKeys.refreshInterval, String(selectedRefresh));
  applyTheme(selectedTheme);
  configureAutoRefresh();
  announceSettings("Preferencias guardadas en este navegador.");
}

function loadPreferences() {
  const storedTheme = localStorage.getItem(storageKeys.theme);
  const parsedInterval = Number.parseInt(localStorage.getItem(storageKeys.refreshInterval) ?? "15", 10);
  return {
    theme: allowedThemes.has(storedTheme) ? storedTheme : "system",
    refreshIntervalSeconds: allowedRefreshIntervals.has(parsedInterval) ? parsedInterval : 15,
  };
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = allowedThemes.has(theme) ? theme : "system";
}

function configureAutoRefresh() {
  if (state.refreshTimer !== null) {
    window.clearInterval(state.refreshTimer);
    state.refreshTimer = null;
  }

  const seconds = state.preferences.refreshIntervalSeconds;
  if (seconds > 0) {
    state.refreshTimer = window.setInterval(() => {
      if (document.visibilityState === "visible") {
        void refreshData({ announce: false });
      }
    }, seconds * 1000);
  }
}

function setGlobalStatus(kind, text) {
  elements.globalStatusDot.className = `status-dot status-${kind}`;
  elements.globalStatusText.textContent = text;
}

function setOverviewBadge(kind, text) {
  elements.overviewBadge.className = `badge badge-${kind}`;
  elements.overviewBadge.textContent = text;
}

function announceSettings(message) {
  elements.settingsMessage.textContent = message;
  window.setTimeout(() => {
    if (elements.settingsMessage.textContent === message) {
      elements.settingsMessage.textContent = "";
    }
  }, 4000);
}

function humanize(value) {
  return String(value)
    .replaceAll("-", " ")
    .replaceAll("_", " ")
    .replace(/\b\w/g, character => character.toUpperCase());
}

function formatDuration(totalSeconds) {
  if (!Number.isFinite(totalSeconds) || totalSeconds < 0) {
    return "—";
  }
  const seconds = Math.floor(totalSeconds);
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) {
    return `${days} d ${hours} h`;
  }
  if (hours > 0) {
    return `${hours} h ${minutes} min`;
  }
  return `${minutes} min`;
}

function formatRefreshTime(date, stale) {
  if (!(date instanceof Date)) {
    return "Sin actualización";
  }
  const prefix = stale ? "Último estado " : "Actualizado ";
  return `${prefix}${new Intl.DateTimeFormat("es-ES", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).format(date)}`;
}

function formatDateTime(date) {
  return new Intl.DateTimeFormat("es-ES", {
    dateStyle: "short",
    timeStyle: "medium",
  }).format(date);
}
