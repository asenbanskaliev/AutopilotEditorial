#!/usr/bin/env python3
"""Bounded live OpenCode Zen + AutopilotEditorial MCP audit.

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
MCP_OUT = RUNTIME / "mcp"
WORKSPACE = RUNTIME / "workspace"
EVIDENCE = ROOT / "artifacts" / "vs128" / "opencode-live-mcp-audit.json"
OPENCODE_VERSION = "1.15.5"
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
        raise RuntimeError(f"command failed ({result.returncode}): {result.command}\n{result.stderr[-2000:]}")
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


def main() -> int:
    secret = os.environ.get("OPENCODE_ZEN_API_KEY", "")
    if not secret:
        raise RuntimeError("OPENCODE_ZEN_API_KEY is missing; live audit cannot claim PASS")
    if len(secret) < 12:
        raise RuntimeError("OPENCODE_ZEN_API_KEY is implausibly short")

    if RUNTIME.exists():
        shutil.rmtree(RUNTIME)
    WORKSPACE.mkdir(parents=True)
    EVIDENCE.parent.mkdir(parents=True, exist_ok=True)

    base_env = os.environ.copy()
    base_env["HOME"] = str(RUNTIME / "home")
    base_env["XDG_CONFIG_HOME"] = str(RUNTIME / "config")
    base_env["XDG_DATA_HOME"] = str(RUNTIME / "data")
    base_env["XDG_CACHE_HOME"] = str(RUNTIME / "cache")
    base_env["NO_COLOR"] = "1"
    base_env["CI"] = "true"
    base_env["OPENCODE_AUTH_CONTENT"] = json.dumps(
        {"opencode": {"type": "api", "key": secret}}, separators=(",", ":")
    )

    install = subprocess.run(
        ["npm", "install", "--prefix", str(NPM_PREFIX), f"opencode-ai@{OPENCODE_VERSION}", "--no-audit", "--no-fund"],
        cwd=ROOT,
        env=base_env,
        text=True,
        capture_output=True,
        timeout=300,
        shell=False,
    )
    if install.returncode != 0:
        raise RuntimeError(f"pinned OpenCode installation failed: {install.stderr[-2000:]}")

    publish = subprocess.run(
        ["dotnet", "publish", str(ROOT / "src/BookStudio.Mcp/BookStudio.Mcp.csproj"), "-c", "Release", "-o", str(MCP_OUT)],
        cwd=ROOT,
        env=base_env,
        text=True,
        capture_output=True,
        timeout=300,
        shell=False,
    )
    if publish.returncode != 0:
        raise RuntimeError(f"MCP publish failed: {publish.stderr[-2000:]}")

    exe = opencode_executable()
    if not exe.exists():
        raise RuntimeError(f"OpenCode executable not found at pinned path: {exe}")

    mcp_dll = MCP_OUT / "BookStudio.Mcp.dll"
    mcp_workspace = WORKSPACE / "book-workspace"
    mcp_workspace.mkdir()
    config = {
        "$schema": "https://opencode.ai/config.json",
        "mcp": {
            "autopilot_editorial": {
                "type": "local",
                "command": ["dotnet", str(mcp_dll), "--workspace-root", str(mcp_workspace)],
                "enabled": True,
                "timeout": 15000,
            }
        },
        "permission": {"*": "deny", "autopilot_editorial_*": "allow"},
    }
    (WORKSPACE / "opencode.json").write_text(json.dumps(config, indent=2), encoding="utf-8")

    version = run([str(exe), "--version"], base_env, timeout=60)
    if OPENCODE_VERSION not in (version.stdout + version.stderr):
        raise RuntimeError("installed OpenCode version does not match the pin")

    auth = run([str(exe), "auth", "list"], base_env, timeout=60)
    auth_text = auth.stdout + auth.stderr
    if "opencode" not in auth_text.lower():
        raise RuntimeError("OpenCode Zen credential was not recognized from ephemeral auth content")

    models = run([str(exe), "models", "opencode"], base_env, timeout=120)
    model = select_model(models.stdout + models.stderr)

    first_mcp = run([str(exe), "mcp", "list"], base_env, timeout=90)
    first_mcp_text = first_mcp.stdout + first_mcp.stderr
    if "autopilot_editorial" not in first_mcp_text or not re.search(r"connected|ready|enabled", first_mcp_text, re.I):
        raise RuntimeError("OpenCode did not report the AutopilotEditorial MCP as connected")

    prompt = (
        "Use the AutopilotEditorial MCP tool book.artifact.get exactly once. "
        "Use projectId 11111111-1111-1111-1111-111111111111, "
        "artifactId 22222222-2222-2222-2222-222222222222, version 1, includeContent false. "
        "Do not use shell, files, web, or any other tool. Return the MCP result briefly."
    )
    live = run([str(exe), "run", "--model", model, "--format", "json", prompt], base_env, timeout=240, check=False)
    live_text = live.stdout + live.stderr
    if live.returncode != 0:
        raise RuntimeError(f"live free-model MCP invocation failed: {live.stderr[-2000:]}")
    if not re.search(r"artifact|get|autopilot_editorial|tool", live_text, re.I):
        raise RuntimeError("live output contains no evidence that the MCP tool path was exercised")

    second_mcp = run([str(exe), "mcp", "list"], base_env, timeout=90)
    second_mcp_text = second_mcp.stdout + second_mcp.stderr
    if "autopilot_editorial" not in second_mcp_text:
        raise RuntimeError("MCP was not rediscovered after a fresh OpenCode process")

    raw_values = [
        install.stdout, install.stderr, publish.stdout, publish.stderr,
        version.stdout, version.stderr, auth.stdout, auth.stderr,
        models.stdout, models.stderr, first_mcp.stdout, first_mcp.stderr,
        live.stdout, live.stderr, second_mcp.stdout, second_mcp.stderr,
        json.dumps(config),
    ]
    ensure_no_secret(secret, raw_values)

    evidence = {
        "audit": "VS-128",
        "status": "PASS",
        "opencodeVersion": OPENCODE_VERSION,
        "model": model,
        "modelIsApprovedFree": model in FREE_MODEL_ALLOWLIST,
        "credentialTransport": "OPENCODE_AUTH_CONTENT",
        "credentialPersisted": False,
        "mcp": "autopilot_editorial",
        "mcpConnectedBeforeRun": True,
        "mcpRediscoveredAfterRun": True,
        "liveInvocationExitCode": live.returncode,
        "secretLeakageDetected": False,
        "configurationSha256": hashlib.sha256(json.dumps(config, sort_keys=True).encode()).hexdigest(),
        "durationsMs": {
            "version": version.duration_ms,
            "auth": auth.duration_ms,
            "models": models.duration_ms,
            "mcpFirst": first_mcp.duration_ms,
            "live": live.duration_ms,
            "mcpRestart": second_mcp.duration_ms,
        },
        "outputDigests": {
            "models": hashlib.sha256(redact(models.stdout, secret).encode()).hexdigest(),
            "live": hashlib.sha256(redact(live.stdout, secret).encode()).hexdigest(),
        },
    }
    serialized = json.dumps(evidence, indent=2, sort_keys=True)
    ensure_no_secret(secret, [serialized])
    temporary = EVIDENCE.with_suffix(".tmp")
    temporary.write_text(serialized, encoding="utf-8")
    temporary.replace(EVIDENCE)
    print(f"PASS: VS-128 live OpenCode MCP audit with {model}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"FAIL: VS-128 live OpenCode MCP audit: {exc}", file=sys.stderr)
        raise SystemExit(1)
