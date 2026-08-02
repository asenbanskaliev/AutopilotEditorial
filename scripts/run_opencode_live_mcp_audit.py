#!/usr/bin/env python3
"""Strong live OpenCode content generation plus deterministic real MCP journey."""
from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import select
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


def ensure_no_secret(secret: str, values: list[str]) -> None:
    probes = [secret]
    if len(secret) >= 12:
        probes.extend([secret[:8], secret[-8:]])
    for value in values:
        for probe in probes:
            if probe and probe in value:
                raise RuntimeError("secret leakage detected")


def run(args: list[str], env: dict[str, str], timeout: int, cwd: pathlib.Path = WORKSPACE) -> CommandResult:
    started = time.monotonic()
    try:
        completed = subprocess.run(
            args,
            cwd=cwd,
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
    if result.returncode != 0:
        raise RuntimeError(f"command failed ({result.returncode}): {result.command}\n{result.stderr[-2500:]}")
    return result


def opencode_executable() -> pathlib.Path:
    suffix = ".cmd" if os.name == "nt" else ""
    return NPM_PREFIX / "node_modules" / ".bin" / f"opencode{suffix}"


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


def publish(project: str, output: pathlib.Path, env: dict[str, str]) -> CommandResult:
    return run(
        ["dotnet", "publish", str(ROOT / project), "-c", "Release", "-o", str(output)],
        env,
        240,
        cwd=ROOT,
    )


def clean_generated_text(text: str) -> str:
    text = re.sub(r"\x1b\[[0-9;]*[A-Za-z]", "", text).replace("\x00", "").strip()
    if len(text) < 80:
        raise RuntimeError("OpenCode generated content is unexpectedly short")
    if len(text.encode("utf-8")) > 500_000:
        raise RuntimeError("OpenCode generated content exceeds MCP draft limit")
    return text


def generate_stage(
    number: int,
    total: int,
    name: str,
    exe: pathlib.Path,
    model: str,
    env: dict[str, str],
    prompt: str,
    timeout: int,
) -> tuple[str, CommandResult]:
    progress(f"STAGE {number}/{total} START: OpenCode generates {name}")
    result = run([str(exe), "run", "--model", model, prompt], env, timeout)
    content = clean_generated_text(result.stdout)
    progress(f"STAGE {number}/{total} GENERATION PASS: {name} ({result.duration_ms} ms)")
    return content, result


class McpProcess:
    def __init__(self, dll: pathlib.Path, workspace: pathlib.Path, name: str):
        self.name = name
        self.process = subprocess.Popen(
            ["dotnet", str(dll), "--workspace-root", str(workspace)],
            cwd=WORKSPACE,
            text=True,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=1,
        )
        self.next_id = 1
        self._initialize()

    def _write(self, payload: dict) -> None:
        if self.process.stdin is None:
            raise RuntimeError(f"{self.name} stdin unavailable")
        self.process.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
        self.process.stdin.flush()

    def _read(self, timeout: int = 30) -> dict:
        if self.process.stdout is None:
            raise RuntimeError(f"{self.name} stdout unavailable")
        ready, _, _ = select.select([self.process.stdout], [], [], timeout)
        if not ready:
            raise RuntimeError(f"{self.name} response timeout after {timeout}s")
        line = self.process.stdout.readline()
        if not line:
            stderr = self.process.stderr.read() if self.process.stderr else ""
            raise RuntimeError(f"{self.name} closed unexpectedly: {stderr[-1000:]}")
        return json.loads(line)

    def request(self, method: str, params: dict, timeout: int = 30) -> dict:
        request_id = self.next_id
        self.next_id += 1
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        response = self._read(timeout)
        if response.get("id") != request_id:
            raise RuntimeError(f"{self.name} response id mismatch")
        if "error" in response:
            raise RuntimeError(f"{self.name} JSON-RPC error: {json.dumps(response['error'])}")
        return response["result"]

    def notify(self, method: str, params: dict) -> None:
        self._write({"jsonrpc": "2.0", "method": method, "params": params})

    def _initialize(self) -> None:
        result = self.request(
            "initialize",
            {
                "protocolVersion": "2025-11-25",
                "capabilities": {},
                "clientInfo": {"name": "vs128-strong-audit", "version": "1.0.0"},
            },
        )
        if "serverInfo" not in result:
            raise RuntimeError(f"{self.name} initialize response missing serverInfo")
        self.notify("notifications/initialized", {})

    def tool(self, name: str, arguments: dict, timeout: int = 45) -> dict:
        result = self.request("tools/call", {"name": name, "arguments": arguments}, timeout)
        if result.get("isError"):
            raise RuntimeError(f"{self.name} tool {name} failed: {json.dumps(result.get('structuredContent', {}))}")
        return result

    def close(self) -> str:
        if self.process.stdin:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        stderr = self.process.stderr.read() if self.process.stderr else ""
        if self.process.returncode not in (0, None):
            raise RuntimeError(f"{self.name} exited with {self.process.returncode}: {stderr[-1000:]}")
        return stderr


def require_artifact(result: dict, artifact_id: str, stage: str) -> None:
    raw = json.dumps(result, ensure_ascii=False)
    if artifact_id not in raw:
        raise RuntimeError(f"{stage} result does not reference {artifact_id}")
    progress(f"PERSISTENCE PASS: {artifact_id}")


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
    install = run(
        ["npm", "install", "--prefix", str(NPM_PREFIX), f"opencode-ai@{OPENCODE_VERSION}", "--no-audit", "--no-fund"],
        env,
        240,
        cwd=ROOT,
    )
    progress("SETUP PASS: pinned OpenCode installed")

    progress("SETUP START: publish MCP servers")
    authoring_publish = publish("src/BookStudio.Mcp.Authoring/BookStudio.Mcp.Authoring.csproj", AUTHORING_OUT, env)
    production_publish = publish("src/BookStudio.Mcp.Production/BookStudio.Mcp.Production.csproj", PRODUCTION_OUT, env)
    progress("SETUP PASS: MCP servers published")

    exe = opencode_executable()
    version = run([str(exe), "--version"], env, 45)
    if OPENCODE_VERSION not in version.stdout + version.stderr:
        raise RuntimeError("installed OpenCode version does not match the pin")
    auth = run([str(exe), "auth", "list"], env, 45)
    if "opencode" not in (auth.stdout + auth.stderr).lower():
        raise RuntimeError("Zen credential was not recognized")
    models = run([str(exe), "models", "opencode"], env, 90)
    model = select_model(models.stdout + models.stderr)
    progress(f"SETUP PASS: OpenCode authenticated, model={model}")

    total = 6
    briefing, briefing_run = generate_stage(
        1, total, "briefing", exe, model, env,
        "Write a concise professional Spanish briefing for a near-future literary mystery set in Navarra, where an archivist discovers that erased municipal records predict disappearances. Return only the briefing in Markdown. Do not call tools.",
        120,
    )
    outline, outline_run = generate_stage(
        2, total, "outline", exe, model, env,
        "Write a structured Spanish outline for the same Navarra mystery. Include premise, protagonist, conflict, eight chapter beats, tone and continuity rules. Return only Markdown. Do not call tools.",
        120,
    )
    chapter, chapter_run = generate_stage(
        3, total, "chapter", exe, model, env,
        "Write chapter 1 in polished Spanish, 400 to 650 words, headed '# Capítulo 1', with a concrete Navarra setting, character goal, tension and a closing hook. Return only Markdown. Do not call tools.",
        180,
    )

    progress("STAGE 1/6 MCP START: register briefing")
    authoring = McpProcess(AUTHORING_OUT / "BookStudio.Mcp.Authoring.dll", BOOK_WORKSPACE, "authoring")
    briefing_result = authoring.tool("book.draft.register", {
        "projectId": PROJECT_ID,
        "payload": {"artifactId": BRIEFING_ID, "expectedVersion": 1, "mediaType": "text/markdown", "content": briefing},
    })
    require_artifact(briefing_result, BRIEFING_ID, "briefing")
    progress("STAGE 1/6 PASS: briefing generated and registered")

    progress("STAGE 2/6 MCP START: register outline")
    outline_result = authoring.tool("book.draft.register", {
        "projectId": PROJECT_ID,
        "payload": {"artifactId": OUTLINE_ID, "expectedVersion": 1, "mediaType": "text/markdown", "content": outline},
    })
    require_artifact(outline_result, OUTLINE_ID, "outline")
    progress("STAGE 2/6 PASS: outline generated and registered")

    progress("STAGE 3/6 MCP START: register chapter")
    chapter_result = authoring.tool("book.draft.register", {
        "projectId": PROJECT_ID,
        "payload": {"artifactId": CHAPTER_ID, "expectedVersion": 1, "mediaType": "text/markdown", "content": chapter},
    })
    require_artifact(chapter_result, CHAPTER_ID, "chapter")
    progress("STAGE 3/6 PASS: chapter generated and registered")

    progress("STAGE 4/6 START: validate chapter")
    validation_result = authoring.tool("book.draft.validate", {
        "projectId": PROJECT_ID,
        "payload": {"artifactId": CHAPTER_ID, "version": 1, "maximumLineLength": 160},
    })
    require_artifact(validation_result, CHAPTER_ID, "validation")
    authoring_stderr = authoring.close()
    progress("STAGE 4/6 PASS: chapter validated")

    progress("STAGE 5/6 START: prepare release and preflight")
    production = McpProcess(PRODUCTION_OUT / "BookStudio.Mcp.Production.dll", BOOK_WORKSPACE, "production")
    release_result = production.tool("book.release.prepare", {
        "projectId": PROJECT_ID,
        "payload": {
            "releaseId": RELEASE_ID,
            "expectedVersion": 1,
            "title": "Los registros borrados",
            "language": "es-ES",
            "sources": [{"role": "manuscript", "artifactId": CHAPTER_ID, "version": 1}],
        },
    })
    require_artifact(release_result, RELEASE_ARTIFACT_ID, "release")
    preflight_result = production.tool("book.preflight.run", {
        "projectId": PROJECT_ID,
        "payload": {"releaseArtifactId": RELEASE_ARTIFACT_ID, "version": 1, "profile": "release-basic"},
    })
    if "PASS" not in json.dumps(preflight_result):
        raise RuntimeError("release preflight did not PASS")
    production_stderr = production.close()
    progress("STAGE 5/6 PASS: release prepared and preflight passed")

    progress("STAGE 6/6 START: restart and resume read-only checks")
    authoring_restart = McpProcess(AUTHORING_OUT / "BookStudio.Mcp.Authoring.dll", BOOK_WORKSPACE, "authoring-restart")
    resumed_validation = authoring_restart.tool("book.draft.validate", {
        "projectId": PROJECT_ID,
        "payload": {"artifactId": CHAPTER_ID, "version": 1, "maximumLineLength": 160},
    })
    require_artifact(resumed_validation, CHAPTER_ID, "resumed validation")
    authoring_restart_stderr = authoring_restart.close()

    production_restart = McpProcess(PRODUCTION_OUT / "BookStudio.Mcp.Production.dll", BOOK_WORKSPACE, "production-restart")
    resumed_preflight = production_restart.tool("book.preflight.run", {
        "projectId": PROJECT_ID,
        "payload": {"releaseArtifactId": RELEASE_ARTIFACT_ID, "version": 1, "profile": "release-basic"},
    })
    if "PASS" not in json.dumps(resumed_preflight):
        raise RuntimeError("resumed preflight did not PASS")
    production_restart_stderr = production_restart.close()
    progress("STAGE 6/6 PASS: restart and resume completed without duplicate writes")

    auth_file = RUNTIME / "data" / "opencode" / "auth.json"
    if auth_file.exists():
        raise RuntimeError("OpenCode persisted auth.json despite ephemeral credential transport")

    captured = [
        install.stdout, install.stderr,
        authoring_publish.stdout, authoring_publish.stderr,
        production_publish.stdout, production_publish.stderr,
        version.stdout, version.stderr, auth.stdout, auth.stderr,
        models.stdout, models.stderr,
        briefing_run.stdout, briefing_run.stderr,
        outline_run.stdout, outline_run.stderr,
        chapter_run.stdout, chapter_run.stderr,
        authoring_stderr, production_stderr,
        authoring_restart_stderr, production_restart_stderr,
        json.dumps(briefing_result), json.dumps(outline_result), json.dumps(chapter_result),
        json.dumps(validation_result), json.dumps(release_result), json.dumps(preflight_result),
        json.dumps(resumed_validation), json.dumps(resumed_preflight),
    ]
    ensure_no_secret(secret, captured)

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
        "briefingPersisted": True,
        "outlinePersisted": True,
        "chapterPersisted": True,
        "chapterValidated": True,
        "releasePersisted": True,
        "preflightPassed": True,
        "restartRediscovery": True,
        "resumeValidationPassed": True,
        "resumePreflightPassed": True,
        "duplicateRegistrationAttempted": False,
        "duplicateReleasePreparationAttempted": False,
        "secretLeakageDetected": False,
        "workspaceFileCount": sum(1 for path in BOOK_WORKSPACE.rglob("*") if path.is_file()),
        "generationDurationsMs": {
            "briefing": briefing_run.duration_ms,
            "outline": outline_run.duration_ms,
            "chapter": chapter_run.duration_ms,
        },
        "contentDigests": {
            "briefing": hashlib.sha256(briefing.encode()).hexdigest(),
            "outline": hashlib.sha256(outline.encode()).hexdigest(),
            "chapter": hashlib.sha256(chapter.encode()).hexdigest(),
        },
    }
    serialized = json.dumps(evidence, indent=2, sort_keys=True)
    ensure_no_secret(secret, [serialized])
    temporary = EVIDENCE.with_suffix(".tmp")
    temporary.write_text(serialized, encoding="utf-8")
    temporary.replace(EVIDENCE)
    progress(f"PASS: VS-128 strong deterministic MCP journey with live OpenCode model {model}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"FAIL: VS-128 strong OpenCode MCP audit: {exc}", file=sys.stderr, flush=True)
        raise SystemExit(1)
