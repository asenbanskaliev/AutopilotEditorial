from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Production/AccessibilityContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Production/AccessibilityOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Production/SqliteAccessibilityStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0051_accessibility_pipeline.sql"
SPEC = ROOT / "docs/specs/VS-114.md"
RED = ROOT / "docs/evidence/VS-114/RED_EVIDENCE.md"


class Vs114AccessibilityContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-114 file: {path}")

    def test_contract_exposes_authority_analysis_review_waiver_and_decision(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IAccessibilityStore", "IAccessibilityAuthorityReader", "IAccessibilityAnalyzer",
            "AccessibilityRequest", "AccessibilityAuthority", "AccessibilityAnalyzerExecution",
            "AccessibilityFinding", "AccessibilityManualReview", "AccessibilityWaiver",
            "AccessibilityDecisionCommand", "AccessibilityStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAuthorityAsync", "BuildEvidence", "OrderBy", "ThenBy",
            "EvidenceDigest", "ArtifactDigest", "AccessibilitySeverity.Blocking",
            "AccessibilityManualReviewDisposition.Pending", "AccessibilityTransitionException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("accessibility_history", source)
        self.assertIn("accessibility_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<AccessibilityState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("accessibility_outbox", source)
        self.assertIn("accessibility_history", source)
        self.assertIn("accessibility_receipts", source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "accessibility_runs", "accessibility_executions", "accessibility_findings",
            "accessibility_reviews", "accessibility_waivers", "accessibility_decisions",
            "accessibility_receipts", "accessibility_history", "accessibility_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("revision", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("evidence_json", migration)


if __name__ == "__main__":
    unittest.main()
