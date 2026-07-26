from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "config" / "ci" / "providers.json"
MAX_CAPTURE_CHARS = 200_000


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8", errors="replace")).hexdigest()


def load_catalog() -> dict:
    return json.loads(CATALOG.read_text(encoding="utf-8"))


def find_by_id(items: list[dict], item_id: str, kind: str) -> dict:
    matches = [item for item in items if item.get("id") == item_id]
    if len(matches) != 1:
        raise ValueError(f"Unknown or duplicate {kind}: {item_id}")
    return matches[0]


def write_evidence(path: Path, evidence: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(evidence, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--provider", required=True)
    parser.add_argument("--contract", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--timeout-seconds", type=int, default=1800)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()

    command = list(args.command)
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        raise SystemExit("A command is required after --")

    catalog = load_catalog()
    provider = find_by_id(catalog["providers"], args.provider, "provider")
    contract = find_by_id(catalog["contracts"], args.contract, "contract")

    if not provider.get("enabled"):
        raise SystemExit(f"Provider is disabled: {args.provider}")
    if provider.get("type") != "local-evidence":
        raise SystemExit("run_local_validation.py only executes local-evidence providers")
    if contract.get("capability") not in provider.get("capabilities", []):
        raise SystemExit("Provider does not satisfy the contract capability")
    if not contract.get("localEquivalentAllowed"):
        raise SystemExit("Local evidence is not approved for this contract")

    started_at = utc_now()
    started_monotonic = time.monotonic()
    stdout = ""
    stderr = ""
    exit_code: int | None = None
    result = "BLOCKED"
    block_reason = ""

    try:
        completed = subprocess.run(
            command,
            cwd=ROOT,
            check=False,
            capture_output=True,
            text=True,
            timeout=args.timeout_seconds,
            shell=False,
            env=os.environ.copy(),
        )
        stdout = completed.stdout
        stderr = completed.stderr
        exit_code = completed.returncode
        result = "PASS" if completed.returncode == 0 else "FAIL"
    except subprocess.TimeoutExpired as exc:
        stdout = exc.stdout or ""
        stderr = exc.stderr or ""
        block_reason = f"timeout after {args.timeout_seconds} seconds"
    except OSError as exc:
        block_reason = f"process could not start: {exc}"

    completed_at = utc_now()
    duration_ms = int((time.monotonic() - started_monotonic) * 1000)

    evidence = {
        "schemaVersion": "1.0.0",
        "providerId": provider["id"],
        "providerType": provider["type"],
        "contractId": contract["id"],
        "sourceSha": args.source_sha,
        "startedAt": started_at,
        "completedAt": completed_at,
        "durationMs": duration_ms,
        "command": command,
        "exitCode": exit_code,
        "result": result,
        "stdoutSha256": sha256_text(stdout),
        "stderrSha256": sha256_text(stderr),
        "stdout": stdout[:MAX_CAPTURE_CHARS],
        "stderr": stderr[:MAX_CAPTURE_CHARS],
        "environment": {
            "python": sys.version.split()[0],
            "platform": platform.platform(),
            "workingDirectory": str(ROOT),
        },
        "equivalence": {
            "mode": "approved-equivalent",
            "approved": True,
            "reason": "The contract explicitly allows local evidence.",
        },
        "retryChain": [],
    }
    if block_reason:
        evidence["equivalence"]["reason"] = block_reason

    write_evidence(args.output, evidence)
    print(f"{result}: {contract['id']} via {provider['id']} -> {args.output}")

    if result == "PASS":
        return 0
    if result == "FAIL" and exit_code is not None:
        return exit_code if exit_code != 0 else 1
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
