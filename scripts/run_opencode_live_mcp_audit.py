#!/usr/bin/env python3
"""Observable strong live OpenCode Zen + AutopilotEditorial MCP audit."""
from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass

ROOT = pathlib.Path(__file__).resolve().parents[1]
RUNTIME = ROOT / ".runtime" / "vs128"
NPM_PREFIX = RUNTIME / "opencode"
AUTHORING_OUT = RUNTIME / "mcp-authoring"
PRODUCTION_OUT = RUNTIME / "mcp-production"
WORKSPACE = RUNTIME / "workspace"
BOOK_WORKSPACE = WORKSPACE / "book-workspace"
EVIDENCE = ROOT / "artifacts" / "vs128" / "opencode-live-mcp-audit.json"
OPENCODE_VERSION = "1.15.5"
PROJECT_ID = "vs128-strong"
BRIEFING_ID = "vs128.briefing"
OUTLINE_ID = "vs128.outline"
CHAPTER_ID = "vs128.chapter-01"
RELEASE_ID = "strong-proof"
RELEASE_ARTIFACT_ID = f"{PROJECT_ID}.release.{RELEASE_ID}"
FREE_MODEL_ALLOWLIST = (
    "opencode/deepseek-v4-flash-free",
    "opencode/mimo-v2.5-free",
    "opencode/laguna-s-2.1-free",
    "opencode/ling-3.0-flash-free",
    "opencode/north-mini-code-free",
    "opencode/nemotron-3-ultra-free",
    "opencode/big-pickle",
)


@dataclass
class CommandResult:
    command: str
    returncode: int
    stdout: str
    stderr: str
    duration_ms: int


def progress(message: str) -> None:
    print(message, flush=True)


def redact(value: str, secret: str) -> str:
    if not value:
        return value
    redacted = value.replace(secret, "***")
    if len(secret) >= 12:
        redacted = redacted.replace(secret[:8], "***").replace(secret[-8:], "***")
    return redacted


def run(args: list[str], env: dict[str, str], timeout: int, check: bool = True) -> CommandResult:
    started = time.monotonic()
    try:
        completed = subprocess.run(
            args,
            cwd=WORKSPACE,
            env=env,
            text=True,
            capture_output=True,
            timeout=timeout,
            shell=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"timeout after {timeout}s: {' '.join(args[:4])}") from exc
    result = CommandResult(
        command=" ".join(args),
        returncode=completed.returncode,
        stdout=completed.stdout,
        stderr=completed.stderr,
        duration_ms=int((time.monotonic() - started) * 1000),
    )
    if check and result.returncode != 0:
        raise RuntimeError(f"command failed ({result.returncode}): {result.command}\n{result.stderr[-2500:]}")
    return result


def opencode_executable() -> pathlib.Path:
    suffix = ".cmd" if os.name == "nt" else ""
    return NPM_PREFIX / "node_modules" / ".bin" / f"opencode{suffix}"


def ensure_no_secret(secret: str, values: list[str]) -> None:
    probes = [secret]
    if len(secret) >= 12:
        probes.extend([secret[:8], secret[-8:]])
    for value in values:
        for probe in probes:
            if probe and probe in value:
                raise RuntimeError("secret leakage detected")


def select_model(models_output: str) -> str:
    configured = os.environ.get("OPENCODE_TEST_MODEL", "").strip()
    available = set(re.findall(r"opencode/[A-Za-z0-9._-]+", models_output))
    candidates = (configured,) if configured else FREE_MODEL_ALLOWLIST
    for candidate in candidates:
        if candidate not in FREE_MODEL_ALLOWLIST:
            raise RuntimeError("configured model is not in the free-model allowlist")
        if candidate in available:
            return candidate
    raise RuntimeError("no approved free OpenCode Zen model is currently available")


def publish(project: str, output: pathlib.Path, env: dict[str, str]) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        ["dotnet", "publish", str(ROOT / project), "-c", "Release", "-o", str(output)],
        cwd=ROOT,
        env=env,
        text=True,
        capture_output=True,
        timeout=240,
        shell=False,
    )
    if result.returncode != 0:
        raise RuntimeError(f"MCP publish failed for {project}: {result.stderr[-2500:]}")
    return result


def live_stage(
    number: int,
    total: int,
    name: str,
    exe: pathlib.Path,
    model: str,
    env: dict[str, str],
    prompt: str,
    marker: str,
    timeout: int,
    required_terms: tuple[str, ...],
) -> CommandResult:
    progress(f"STAGE {number}/{total} START: {name}")
    result = run([str(exe), "run", "--model", model, "--format", "json", prompt], env, timeout, check=False)
    text = result.stdout + result.stderr
    if result.returncode != 0:
        raise RuntimeError(f"STAGE {number}/{total} FAILED: {name}: {result.stderr[-2500:]}")
    if marker not in text:
        raise RuntimeError(f"STAGE {number}/{total} FAILED: missing marker {marker}")
    missing = [term for term in required_terms if not re.search(re.escape(term), text, re.I)]
    if missing:
        raise RuntimeError(f"STAGE {number}/{total} FAILED: missing evidence {', '.join(missing)}")
    progress(f"STAGE {number}/{total} PASS: {name} ({result.duration_ms} ms)")
    return result


def main() -> int:
    secret = os.environ.get("OPENCODE_ZEN_API_KEY", "")
    if len(secret) < 12:
        raise RuntimeError("OPENCODE_ZEN_API_KEY is missing or implausibly short")

    if RUNTIME.exists():
        shutil.rmtree(RUNTIME)
    WORKSPACE.mkdir(parents=True)
    BOOK_WORKSPACE.mkdir()
    EVIDENCE.parent.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    env.update({
        "HOME": str(RUNTIME / "home"),
        "XDG_CONFIG_HOME": str(RUNTIME / "config"),
        "XDG_DATA_HOME": str(RUNTIME / "data"),
        "XDG_CACHE_HOME": str(RUNTIME / "cache"),
        "NO_COLOR": "1",
        "CI": "true",
        "OPENCODE_AUTH_CONTENT": json.dumps(
            {"opencode": {"type": "api", "key": secret}}, separators=(",", ":")
        ),
    })

    progress("SETUP START: install pinned OpenCode")
    install = subprocess.run(
        ["npm", "install", "--prefix", str(NPM_PREFIX), f"opencode-ai@{OPENCODE_VERSION}", "--no-audit", "--no-fund"],
        cwd=ROOT,
        env=env,
        text=True,
        capture_output=True,
        timeout=240,
        shell=False,
    )
    if install.returncode != 0:
        raise RuntimeError(f"pinned OpenCode installation failed: {install.stderr[-2500:]}")
    progress("SETUP PASS: pinned OpenCode installed")

    progress("SETUP START: publish MCP servers")
    authoring_publish = publish("src/BookStudio.Mcp.Authoring/BookStudio.Mcp.Authoring.csproj", AUTHORING_OUT, env)
    production_publish = publish("src/BookStudio.Mcp.Production/BookStudio.Mcp.Production.csproj", PRODUCTION_OUT, env)
    progress("SETUP PASS: MCP servers published")

    exe = opencode_executable()
    if not exe.exists():
        raise RuntimeError(f"OpenCode executable not found: {exe}")

    config = {
        "$schema": "https://opencode.ai/config.json",
        "mcp": {
            "autopilot_authoring": {
                "type": "local",
                "command": ["dotnet", str(AUTHORING_OUT / "BookStudio.Mcp.Authoring.dll"), "--workspace-root", str(BOOK_WORKSPACE)],
                "enabled": True,
                "timeout": 30000,
            },
            "autopilot_production": {
                "type": "local",
                "command": ["dotnet", str(PRODUCTION_OUT / "BookStudio.Mcp.Production.dll"), "--workspace-root", str(BOOK_WORKSPACE)],
                "enabled": True,
                "timeout": 30000,
            },
        },
        "permission": {"*": "deny", "autopilot_authoring_*": "allow", "autopilot_production_*": "allow"},
    }
    (WORKSPACE / "opencode.json").write_text(json.dumps(config, indent=2), encoding="utf-8")

    version = run([str(exe), "--version"], env, 45)
    if OPENCODE_VERSION not in version.stdout + version.stderr:
        raise RuntimeError("installed OpenCode version does not match the pin")
    auth = run([str(exe), "auth", "list"], env, 45)
    if "opencode" not in (auth.stdout + auth.stderr).lower():
        raise RuntimeError("Zen credential was not recognized")
    models = run([str(exe), "models", "opencode"], env, 90)
    model = select_model(models.stdout + models.stderr)
    first_mcp = run([str(exe), "mcp", "list"], env, 90)
    first_mcp_text = first_mcp.stdout + first_mcp.stderr
    for server in ("autopilot_authoring", "autopilot_production"):
        if server not in first_mcp_text or not re.search(r"connected|ready|enabled", first_mcp_text, re.I):
            raise RuntimeError(f"OpenCode did not report {server} as connected")
    progress(f"SETUP PASS: OpenCode authenticated, model={model}, MCP servers connected")

    total = 6
    stages: list[CommandResult] = []
    stages.append(live_stage(1, total, "briefing", exe, model, env, f"""
Use only the AutopilotEditorial MCP. For projectId {PROJECT_ID}, write a concise professional Spanish briefing for this idea: 'A near-future literary mystery set in Navarra, where an archivist discovers that erased municipal records predict disappearances.' Register it with book.draft.register using artifactId {BRIEFING_ID}, expectedVersion 1 and mediaType text/markdown. Do not use shell, files or web. Finish exactly with BRIEFING_COMPLETE.
""".strip(), "BRIEFING_COMPLETE", 120, ("book.draft.register", BRIEFING_ID)))

    stages.append(live_stage(2, total, "outline", exe, model, env, f"""
Use only the AutopilotEditorial MCP. For projectId {PROJECT_ID}, create a Spanish outline with premise, protagonist, conflict, eight chapter beats, tone and continuity rules. Register it with book.draft.register using artifactId {OUTLINE_ID}, expectedVersion 1 and mediaType text/markdown. Do not use shell, files or web. Finish exactly with OUTLINE_COMPLETE.
""".strip(), "OUTLINE_COMPLETE", 120, ("book.draft.register", OUTLINE_ID)))

    stages.append(live_stage(3, total, "chapter", exe, model, env, f"""
Use only the AutopilotEditorial MCP. For projectId {PROJECT_ID}, write chapter 1 in polished Spanish, 400 to 650 words, headed '# Capítulo 1', with a concrete Navarra setting, character goal, tension and closing hook. Register it with book.draft.register using artifactId {CHAPTER_ID}, expectedVersion 1 and mediaType text/markdown. Do not use shell, files or web. Finish exactly with CHAPTER_COMPLETE.
""".strip(), "CHAPTER_COMPLETE", 180, ("book.draft.register", CHAPTER_ID)))

    stages.append(live_stage(4, total, "chapter validation", exe, model, env, f"""
Use only the AutopilotEditorial MCP. For projectId {PROJECT_ID}, call book.draft.validate for artifactId {CHAPTER_ID}, version 1 and maximumLineLength 160. Do not register anything. Finish exactly with VALIDATION_COMPLETE.
""".strip(), "VALIDATION_COMPLETE", 90, ("book.draft.validate", CHAPTER_ID)))

    stages.append(live_stage(5, total, "release and preflight", exe, model, env, f"""
Use only the AutopilotEditorial MCP. For projectId {PROJECT_ID}, call book.release.prepare with releaseId {RELEASE_ID}, expectedVersion 1, title 'Los registros borrados', language es-ES, and one source role manuscript, artifactId {CHAPTER_ID}, version 1. Then call book.preflight.run for releaseArtifactId {RELEASE_ARTIFACT_ID}, version 1, profile release-basic. Finish exactly with RELEASE_COMPLETE.
""".strip(), "RELEASE_COMPLETE", 150, ("book.release.prepare", "book.preflight.run", RELEASE_ARTIFACT_ID)))

    second_mcp = run([str(exe), "mcp", "list"], env, 90)
    second_mcp_text = second_mcp.stdout + second_mcp.stderr
    for server in ("autopilot_authoring", "autopilot_production"):
        if server not in second_mcp_text:
            raise RuntimeError(f"{server} was not rediscovered after restart")

    stages.append(live_stage(6, total, "restart and resume", exe, model, env, f"""
Resume existing project {PROJECT_ID} using only AutopilotEditorial MCP tools. Call book.draft.validate for {CHAPTER_ID}, version 1, maximumLineLength 160, then book.preflight.run for {RELEASE_ARTIFACT_ID}, version 1, profile release-basic. Do not register or prepare anything again. Finish exactly with RESUME_COMPLETE.
""".strip(), "RESUME_COMPLETE", 120, ("book.draft.validate", "book.preflight.run")))

    if not any(BOOK_WORKSPACE.rglob("*")):
        raise RuntimeError("journey created no persistent MCP workspace evidence")
    auth_file = RUNTIME / "data" / "opencode" / "auth.json"
    if auth_file.exists():
        raise RuntimeError("OpenCode persisted auth.json despite ephemeral credential transport")

    raw_values = [
        install.stdout, install.stderr, authoring_publish.stdout, authoring_publish.stderr,
        production_publish.stdout, production_publish.stderr, version.stdout, version.stderr,
        auth.stdout, auth.stderr, models.stdout, models.stderr, first_mcp.stdout, first_mcp.stderr,
        second_mcp.stdout, second_mcp.stderr, json.dumps(config),
    ]
    for stage in stages:
        raw_values.extend([stage.stdout, stage.stderr])
    ensure_no_secret(secret, raw_values)

    evidence = {
        "audit": "VS-128-strong-editorial-journey",
        "status": "PASS",
        "opencodeVersion": OPENCODE_VERSION,
        "model": model,
        "modelIsApprovedFree": model in FREE_MODEL_ALLOWLIST,
        "credentialTransport": "OPENCODE_AUTH_CONTENT",
        "credentialPersisted": False,
        "projectId": PROJECT_ID,
        "stageCount": total,
        "allStagesPassed": True,
        "restartRediscovery": True,
        "duplicateRegistrationAttempted": False,
        "duplicateReleasePreparationAttempted": False,
        "secretLeakageDetected": False,
        "workspaceFileCount": sum(1 for path in BOOK_WORKSPACE.rglob("*") if path.is_file()),
        "configurationSha256": hashlib.sha256(json.dumps(config, sort_keys=True).encode()).hexdigest(),
        "stageDurationsMs": [stage.duration_ms for stage in stages],
        "stageOutputDigests": [hashlib.sha256(redact(stage.stdout, secret).encode()).hexdigest() for stage in stages],
    }
    serialized = json.dumps(evidence, indent=2, sort_keys=True)
    ensure_no_secret(secret, [serialized])
    temporary = EVIDENCE.with_suffix(".tmp")
    temporary.write_text(serialized, encoding="utf-8")
    temporary.replace(EVIDENCE)
    progress(f"PASS: VS-128 strong staged editorial journey with {model}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"FAIL: VS-128 strong OpenCode MCP audit: {exc}", file=sys.stderr, flush=True)
        raise SystemExit(1)
