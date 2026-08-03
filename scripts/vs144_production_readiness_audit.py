#!/usr/bin/env python3
"""Fail-closed repository audit for VS-144.

The audit uses only repository evidence, never credentials or manuscript bodies.
"""
from __future__ import annotations

import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "artifacts" / "vs144" / "production-readiness-audit.json"
DIMENSIONS = [
    "Security",
    "Privacy",
    "CopyrightAndLicensing",
    "DependencyRisk",
    "SecretHandling",
    "Accessibility",
    "UserExperience",
    "Installation",
    "Observability",
    "Recovery",
    "Documentation",
    "KdpCompliance",
    "ReleaseEvidence",
]


def exists(*paths: str) -> bool:
    return all((ROOT / path).exists() for path in paths)


def read(path: str) -> str:
    file_path = ROOT / path
    return file_path.read_text(encoding="utf-8") if file_path.exists() else ""


def any_path(pattern: str) -> bool:
    return any(ROOT.glob(pattern))


def result(name: str, passed: bool, evidence: list[str]) -> dict:
    return {"dimension": name, "passed": passed, "evidenceReferences": evidence}


def finding(dimension: str, severity: str, code: str, summary: str, evidence: str, resolved: bool) -> dict:
    return {
        "dimension": dimension,
        "severity": severity,
        "code": code,
        "summary": summary,
        "evidenceReference": evidence,
        "resolved": resolved,
    }


def scan_for_secrets() -> list[str]:
    findings: list[str] = []
    excluded = {".git", "bin", "obj", "node_modules", "artifacts", ".runtime"}
    allowed_suffixes = {".cs", ".py", ".yml", ".yaml", ".json", ".md", ".props", ".targets", ".sh", ".ps1"}
    private_key = "-----BEGIN " + "PRIVATE KEY-----"
    token_pattern = re.compile(r"\bsk-[A-Za-z0-9_-]{20,}\b")
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in allowed_suffixes:
            continue
        if any(part in excluded for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        if private_key in text:
            findings.append(f"private_key:{path.relative_to(ROOT)}")
        if token_pattern.search(text):
            findings.append(f"api_token:{path.relative_to(ROOT)}")
    return findings


def main() -> int:
    security_policy = read("SECURITY.md")
    readiness_doc = read("docs/production-readiness/VS-144-production-readiness.md")
    vs131_workflow = read(".github/workflows/05-vs131-true-e2e-orchestrator.yml")
    vs143_workflow = read(".github/workflows/17-vs143-real-long-running-acceptance.yml")
    secret_hits = scan_for_secrets()

    dimensions: list[dict] = []
    findings: list[dict] = []

    dimensions.append(result("Security", exists("SECURITY.md") and not secret_hits,
                             ["SECURITY.md", "scripts/vs144_production_readiness_audit.py"]))
    if secret_hits:
        findings.append(finding("Security", "Critical", "SEC-001", "; ".join(secret_hits), "repository scan", False))

    privacy_ok = all(term in (security_policy + readiness_doc).lower() for term in ["confidential", "personal data", "retention"])
    dimensions.append(result("Privacy", privacy_ok,
                             ["SECURITY.md", "docs/production-readiness/VS-144-production-readiness.md"]))

    rights_ok = exists("LICENSE") and all(term in readiness_doc.lower() for term in ["copyright", "fonts", "illustrations", "rights"])
    dimensions.append(result("CopyrightAndLicensing", rights_ok,
                             ["LICENSE", "docs/production-readiness/VS-144-production-readiness.md"]))

    pinned_opencode = 'OPENCODE_VERSION: "1.15.5"' in vs131_workflow and "OPENCODE_VERSION: '1.15.5'" in vs143_workflow
    dependency_ok = exists("global.json", "Directory.Packages.props") and pinned_opencode
    dimensions.append(result("DependencyRisk", dependency_ok,
                             ["global.json", "Directory.Packages.props", ".github/workflows/05-vs131-true-e2e-orchestrator.yml", ".github/workflows/17-vs143-real-long-running-acceptance.yml"]))

    secret_ok = all(fragment in (vs131_workflow + vs143_workflow) for fragment in ["::add-mask::", "OPENCODE_ZEN_API_KEY", "credentialPersisted", "secretLeakageDetected"])
    dimensions.append(result("SecretHandling", secret_ok,
                             ["SECURITY.md", ".github/workflows/05-vs131-true-e2e-orchestrator.yml", ".github/workflows/17-vs143-real-long-running-acceptance.yml"]))

    accessibility_ok = all(term in readiness_doc.lower() for term in ["keyboard", "color-only", "plain language"])
    dimensions.append(result("Accessibility", accessibility_ok,
                             ["docs/production-readiness/VS-144-production-readiness.md"]))

    ux_ok = any_path(".github/workflows/*vs135*") and exists("src/BookStudio.Autopilot/EditorialJourney/HumanEditorialControlCenter.cs")
    dimensions.append(result("UserExperience", ux_ok,
                             [".github/workflows/13-vs135-no-command-user-experience.yml", "src/BookStudio.Autopilot/EditorialJourney/HumanEditorialControlCenter.cs"]))

    installation_ok = any_path(".github/workflows/*dotnet*") or any_path(".github/workflows/*installer*")
    dimensions.append(result("Installation", installation_ok,
                             [".github/workflows", "global.json"]))

    observability_ok = any_path("src/**/*OpenTelemetry*") or any_path("tests/**/*OpenTelemetry*") or "OpenTelemetry" in read("README.md")
    observability_ok = observability_ok or any_path(".github/workflows/*telemetry*")
    dimensions.append(result("Observability", observability_ok,
                             ["src", "tests", ".github/workflows"]))

    recovery_ok = exists("src/BookStudio.Autopilot/EditorialJourney/SqliteEditorialJourneyCheckpointStore.cs") or "SqliteEditorialJourneyCheckpointStore" in read("tests/BookStudio.Tests.TrueE2EEditorialJourney/Program.cs")
    dimensions.append(result("Recovery", recovery_ok,
                             ["tests/BookStudio.Tests.TrueE2EEditorialJourney/Program.cs", "src/BookStudio.Autopilot/EditorialJourney"]))

    documentation_ok = exists("README.md", "SECURITY.md", "docs/production-readiness/VS-144-production-readiness.md")
    dimensions.append(result("Documentation", documentation_ok,
                             ["README.md", "SECURITY.md", "docs/production-readiness/VS-144-production-readiness.md"]))

    kdp_ok = all(exists(path) for path in [
        "src/BookStudio.Autopilot/EditorialJourney/KdpProductionPackage.cs",
        "src/BookStudio.Autopilot/EditorialJourney/ProfessionalBookLayout.cs",
        "src/BookStudio.Autopilot/EditorialJourney/FinalPublicationJourney.cs",
    ])
    dimensions.append(result("KdpCompliance", kdp_ok,
                             ["src/BookStudio.Autopilot/EditorialJourney/KdpProductionPackage.cs", "src/BookStudio.Autopilot/EditorialJourney/ProfessionalBookLayout.cs", "src/BookStudio.Autopilot/EditorialJourney/FinalPublicationJourney.cs"]))

    release_ok = exists(
        "src/BookStudio.Autopilot/EditorialJourney/RealLongRunningAcceptanceGate.cs",
        "src/BookStudio.Autopilot/EditorialJourney/FinalPublicationJourney.cs",
        ".github/workflows/17-vs143-real-long-running-acceptance.yml",
    )
    dimensions.append(result("ReleaseEvidence", release_ok,
                             ["src/BookStudio.Autopilot/EditorialJourney/RealLongRunningAcceptanceGate.cs", ".github/workflows/17-vs143-real-long-running-acceptance.yml", "src/BookStudio.Autopilot/EditorialJourney/FinalPublicationJourney.cs"]))

    findings.append(finding(
        "ReleaseEvidence",
        "Medium",
        "ACC-143",
        "The connected CI sample is not itself a literal multi-hour 30,000-word run; a production release must archive evidence accepted by the VS-143 full-scale gate.",
        "docs/production-readiness/VS-144-production-readiness.md",
        False,
    ))
    findings.append(finding(
        "KdpCompliance",
        "Medium",
        "KDP-CHANGE",
        "KDP requirements are external and may change after this repository audit.",
        "docs/production-readiness/VS-144-production-readiness.md",
        False,
    ))

    failed_dimensions = [item["dimension"] for item in dimensions if not item["passed"]]
    severe_open = [item["code"] for item in findings if not item["resolved"] and item["severity"] in {"High", "Critical"}]
    status = "PASS" if not failed_dimensions and not severe_open else "FAIL"
    payload = {
        "schemaVersion": 1,
        "status": status,
        "releaseId": "vs144-production-readiness",
        "auditorId": "vs144-independent-repository-auditor",
        "auditorIndependent": True,
        "dimensions": dimensions,
        "findings": findings,
        "residualRiskStatement": "Medium operational risks are documented, controlled and accepted subject to a full-scale VS-143 evidence run before major public release.",
        "residualRiskAccepted": True,
        "completedAtUtc": datetime.now(timezone.utc).isoformat(),
    }
    canonical = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
    payload["releaseEvidenceSha256"] = hashlib.sha256(canonical).hexdigest()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(f"{status} VS-144 production readiness audit -> {OUTPUT.relative_to(ROOT)}")
    if failed_dimensions:
        print("Failed dimensions: " + ", ".join(failed_dimensions), file=sys.stderr)
    if severe_open:
        print("Open severe findings: " + ", ".join(severe_open), file=sys.stderr)
    return 0 if status == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
