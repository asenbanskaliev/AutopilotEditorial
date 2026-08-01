from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Production/TechnicalPreflightContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Production/TechnicalPreflightOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Production/SqliteTechnicalPreflightStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0052_technical_preflight.sql"
SPEC = ROOT / "docs/specs/VS-115.md"
RED = ROOT / "docs/evidence/VS-115/RED_EVIDENCE.md"


class Vs115TechnicalPreflightContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-115 file: {path}")

    def test_contract_exposes_authority_checker_finding_waiver_and_decision(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "ITechnicalPreflightStore", "ITechnicalPreflightAuthorityReader", "ITechnicalPreflightChecker",
            "TechnicalPreflightRequest", "TechnicalPreflightAuthority", "TechnicalPreflightCheckResult",
            "TechnicalPreflightFinding", "TechnicalPreflightWaiver", "TechnicalPreflightDecisionCommand",
            "TechnicalPreflightStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAsync", "BuildEvidenceDigest", "OrderBy", "ThenBy",
            "InputDigest", "OutputDigest", "TechnicalPreflightSeverity.Blocking",
            "TechnicalPreflightTransitionException", "TechnicalPreflightAuthorityStatus.Approved",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("technical_preflight_history", source)
        self.assertIn("technical_preflight_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<TechnicalPreflightState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("technical_preflight_outbox", source)
        self.assertIn("technical_preflight_history", source)
        self.assertIn("technical_preflight_receipts", source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "technical_preflight_runs", "technical_preflight_executions", "technical_preflight_findings",
            "technical_preflight_waivers", "technical_preflight_decisions", "technical_preflight_receipts",
            "technical_preflight_history", "technical_preflight_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("revision", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("evidence_digest", migration)
        self.assertIn("snapshot_json", migration)


if __name__ == "__main__":
    unittest.main()
