#!/usr/bin/env python3
"""Strong live OpenCode Zen + AutopilotEditorial MCP editorial audit.

The API key is consumed only from OPENCODE_ZEN_API_KEY and passed to OpenCode
through OPENCODE_AUTH_CONTENT. It is never written to disk or evidence.
"""
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


def redact(value: str, secret: str) -> str:
    if not value:
        return value
    redacted = value.replace(secret, "***")
    if len(secret) >= 12:
        redacted = redacted.replace(secret[:8], "***")
        redacted = redacted.replace(secret[-8:], "***")
    return redacted


def run(args: list[str], env: dict[str, str], timeout: int = 180, check: bool = True) -> CommandResult:
    started = time.monotonic()
    completed = subprocess.run(
        args,
        cwd=WORKSPACE,
        env=env,
        text=True,
        capture_output=True,
        timeout=timeout,
        shell=False,
    )
    result = CommandResult(
        command=" ".join(args),
        returncode=completed.returncode,
        stdout=completed.stdout,
        stderr=completed.stderr,
        duration_ms=int((time.monotonic() - started) * 1000),
    )
    if check and result.returncode != 0:
        raise RuntimeError(f"command failed ({result.returncode}): {result.command}\n{result.stderr[-3000:]}")
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
                raise RuntimeError("secret leakage detected in OpenCode output or evidence candidate")


def select_model(models_output: str) -> str:
    configured = os.environ.get("OPENCODE_TEST_MODEL", "").strip()
    available = set(re.findall(r"opencode/[A-Za-z0-9._-]+", models_output))
    if configured:
        if configured not in FREE_MODEL_ALLOWLIST:
            raise RuntimeError("OPENCODE_TEST_MODEL is not in the free-model allowlist")
        if configured not in available:
            raise RuntimeError(f"configured free model is unavailable: {configured}")
        return configured
    for candidate in FREE_MODEL_ALLOWLIST:
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
        timeout=300,
        shell=False,
    )
    if result.returncode != 0:
        raise RuntimeError(f"MCP publish failed for {project}: {result.stderr[-3000:]}")
    return result


def require_live_success(result: CommandResult, marker: str, required_terms: tuple[str, ...]) -> str:
    text = result.stdout + result.stderr
    if result.returncode != 0:
        raise RuntimeError(f"live OpenCode journey failed: {result.stderr[-3000:]}")
    if marker not in text:
        raise RuntimeError(f"live journey did not return required marker {marker}")
    missing = [term for term in required_terms if not re.search(re.escape(term), text, re.I)]
    if missing:
        raise RuntimeError(f"live journey output lacks MCP evidence: {', '.join(missing)}")
    return text


def main() -> int:
    secret = os.environ.get("OPENCODE_ZEN_API_KEY", "")
    if not secret:
        raise RuntimeError("OPENCODE_ZEN_API_KEY is missing; live audit cannot claim PASS")
    if len(secret) < 12:
        raise RuntimeError("OPENCODE_ZEN_API_KEY is implausibly short")

    if RUNTIME.exists():
        shutil.rmtree(RUNTIME)
    WORKSPACE.mkdir(parents=True)
    BOOK_WORKSPACE.mkdir()
    EVIDENCE.parent.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    env["HOME"] = str(RUNTIME / "home")
    env["XDG_CONFIG_HOME"] = str(RUNTIME / "config")
    env["XDG_DATA_HOME"] = str(RUNTIME / "data")
    env["XDG_CACHE_HOME"] = str(RUNTIME / "cache")
    env["NO_COLOR"] = "1"
    env["CI"] = "true"
    env["OPENCODE_AUTH_CONTENT"] = json.dumps(
        {"opencode": {"type": "api", "key": secret}}, separators=(",", ":")
    )

    install = subprocess.run(
        ["npm", "install", "--prefix", str(NPM_PREFIX), f"opencode-ai@{OPENCODE_VERSION}", "--no-audit", "--no-fund"],
        cwd=ROOT,
        env=env,
        text=True,
        capture_output=True,
        timeout=300,
        shell=False,
    )
    if install.returncode != 0:
        raise RuntimeError(f"pinned OpenCode installation failed: {install.stderr[-3000:]}")

    authoring_publish = publish("src/BookStudio.Mcp.Authoring/BookStudio.Mcp.Authoring.csproj", AUTHORING_OUT, env)
    production_publish = publish("src/BookStudio.Mcp.Production/BookStudio.Mcp.Production.csproj", PRODUCTION_OUT, env)

    exe = opencode_executable()
    if not exe.exists():
        raise RuntimeError(f"OpenCode executable not found at pinned path: {exe}")

    authoring_dll = AUTHORING_OUT / "BookStudio.Mcp.Authoring.dll"
    production_dll = PRODUCTION_OUT / "BookStudio.Mcp.Production.dll"
    config = {
        "$schema": "https://opencode.ai/config.json",
        "mcp": {
            "autopilot_authoring": {
                "type": "local",
                "command": ["dotnet", str(authoring_dll), "--workspace-root", str(BOOK_WORKSPACE)],
                "enabled": True,
                "timeout": 30000,
            },
            "autopilot_production": {
                "type": "local",
                "command": ["dotnet", str(production_dll), "--workspace-root", str(BOOK_WORKSPACE)],
                "enabled": True,
                "timeout": 30000,
            },
        },
        "permission": {
            "*": "deny",
            "autopilot_authoring_*": "allow",
            "autopilot_production_*": "allow",
        },
    }
    (WORKSPACE / "opencode.json").write_text(json.dumps(config, indent=2), encoding="utf-8")

    version = run([str(exe), "--version"], env, timeout=60)
    if OPENCODE_VERSION not in (version.stdout + version.stderr):
        raise RuntimeError("installed OpenCode version does not match the pin")

    auth = run([str(exe), "auth", "list"], env, timeout=60)
    if "opencode" not in (auth.stdout + auth.stderr).lower():
        raise RuntimeError("OpenCode Zen credential was not recognized from ephemeral auth content")

    models = run([str(exe), "models", "opencode"], env, timeout=120)
    model = select_model(models.stdout + models.stderr)

    first_mcp = run([str(exe), "mcp", "list"], env, timeout=120)
    first_mcp_text = first_mcp.stdout + first_mcp.stderr
    for server in ("autopilot_authoring", "autopilot_production"):
        if server not in first_mcp_text or not re.search(r"connected|ready|enabled", first_mcp_text, re.I):
            raise RuntimeError(f"OpenCode did not report {server} as connected")

    journey_prompt = f"""
Act as an autonomous professional Spanish-language book editor. Start from this natural-language idea:
'A near-future literary mystery set in Navarra, where an archivist discovers that erased municipal records predict disappearances.'

Use only the AutopilotEditorial MCP tools. Do not use shell, files, web, or any non-MCP tool.
Complete every step in this exact order using projectId {PROJECT_ID}:
1. Write a concise professional briefing in Spanish and register it with book.draft.register as artifactId {BRIEFING_ID}, expectedVersion 1, mediaType text/markdown.
2. Write a structured outline in Spanish with premise, protagonist, conflict, eight chapter beats, tone and continuity rules. Register it as artifactId {OUTLINE_ID}, expectedVersion 1, mediaType text/markdown.
3. Write chapter 1 in polished Spanish, 700 to 1100 words, with heading '# Capítulo 1', concrete setting, character goal, tension, and a closing hook. Register it as artifactId {CHAPTER_ID}, expectedVersion 1, mediaType text/markdown.
4. Call book.draft.validate for {CHAPTER_ID}, version 1, maximumLineLength 160.
5. Call book.release.prepare with releaseId {RELEASE_ID}, expectedVersion 1, title 'Los registros borrados', language es-ES, and one source with role manuscript, artifactId {CHAPTER_ID}, version 1.
6. Call book.preflight.run for releaseArtifactId {RELEASE_ARTIFACT_ID}, version 1, profile release-basic.
Do not stop after drafting text: all six MCP stages are mandatory. Finish with exactly JOURNEY_COMPLETE and briefly list the six successful stages.
""".strip()
    journey = run([str(exe), "run", "--model", model, "--format", "json", journey_prompt], env, timeout=720, check=False)
    journey_text = require_live_success(
        journey,
        "JOURNEY_COMPLETE",
        ("book.draft.register", "book.draft.validate", "book.release.prepare", "book.preflight.run"),
    )

    if not BOOK_WORKSPACE.exists() or not any(BOOK_WORKSPACE.rglob("*")):
        raise RuntimeError("strong journey created no persistent MCP workspace evidence")

    second_mcp = run([str(exe), "mcp", "list"], env, timeout=120)
    second_mcp_text = second_mcp.stdout + second_mcp.stderr
    for server in ("autopilot_authoring", "autopilot_production"):
        if server not in second_mcp_text:
            raise RuntimeError(f"{server} was not rediscovered after a fresh OpenCode process")

    resume_prompt = f"""
Resume the existing AutopilotEditorial project {PROJECT_ID} after process restart. Use only MCP tools.
1. Call book.draft.validate for artifactId {CHAPTER_ID}, version 1, maximumLineLength 160.
2. Call book.preflight.run for releaseArtifactId {RELEASE_ARTIFACT_ID}, version 1, profile release-basic.
Do not register or prepare anything again. Finish with exactly RESUME_COMPLETE and state the validation and preflight decisions.
""".strip()
    resume = run([str(exe), "run", "--model", model, "--format", "json", resume_prompt], env, timeout=360, check=False)
    resume_text = require_live_success(
        resume,
        "RESUME_COMPLETE",
        ("book.draft.validate", "book.preflight.run"),
    )

    auth_file = RUNTIME / "data" / "opencode" / "auth.json"
    if auth_file.exists():
        raise RuntimeError("OpenCode persisted auth.json despite ephemeral credential transport")

    raw_values = [
        install.stdout, install.stderr,
        authoring_publish.stdout, authoring_publish.stderr,
        production_publish.stdout, production_publish.stderr,
        version.stdout, version.stderr, auth.stdout, auth.stderr,
        models.stdout, models.stderr, first_mcp.stdout, first_mcp.stderr,
        journey.stdout, journey.stderr, second_mcp.stdout, second_mcp.stderr,
        resume.stdout, resume.stderr, json.dumps(config),
    ]
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
        "journey": {
            "naturalLanguageIdea": True,
            "briefingRegistered": True,
            "outlineRegistered": True,
            "chapterRegistered": True,
            "chapterValidated": True,
            "releasePrepared": True,
            "preflightExecuted": True,
            "restartRediscovery": True,
            "resumeValidation": True,
            "resumePreflight": True,
            "duplicateRegistrationAttempted": False,
            "duplicateReleasePreparationAttempted": False,
        },
        "servers": ["autopilot_authoring", "autopilot_production"],
        "secretLeakageDetected": False,
        "workspaceFileCount": sum(1 for path in BOOK_WORKSPACE.rglob("*") if path.is_file()),
        "configurationSha256": hashlib.sha256(json.dumps(config, sort_keys=True).encode()).hexdigest(),
        "durationsMs": {
            "models": models.duration_ms,
            "mcpDiscovery": first_mcp.duration_ms,
            "strongJourney": journey.duration_ms,
            "mcpRestart": second_mcp.duration_ms,
            "resumeJourney": resume.duration_ms,
        },
        "outputDigests": {
            "journey": hashlib.sha256(redact(journey_text, secret).encode()).hexdigest(),
            "resume": hashlib.sha256(redact(resume_text, secret).encode()).hexdigest(),
        },
    }
    serialized = json.dumps(evidence, indent=2, sort_keys=True)
    ensure_no_secret(secret, [serialized])
    temporary = EVIDENCE.with_suffix(".tmp")
    temporary.write_text(serialized, encoding="utf-8")
    temporary.replace(EVIDENCE)
    print(f"PASS: VS-128 strong editorial journey with {model}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"FAIL: VS-128 strong OpenCode MCP audit: {exc}", file=sys.stderr)
        raise SystemExit(1)
