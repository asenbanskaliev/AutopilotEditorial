from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Authoring/VisualAccessibilityContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Authoring/VisualAccessibilityOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteVisualAccessibilityStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0046_visual_accessibility.sql"
SPEC = ROOT / "docs/specs/VS-105.md"
RED = ROOT / "docs/evidence/VS-105/RED_EVIDENCE.md"


class Vs105VisualAccessibilityContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-105 file: {path}")

    def test_contract_exposes_authority_assessment_and_decision_lifecycle(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IVisualAccessibilityStore",
            "IVisualAccessibilityAuthorityReader",
            "VisualAccessibilityCaseDraft",
            "VisualAccessibilityAssessment",
            "ContrastEvidence",
            "VisualAccessibilityDecisionCommand",
            "VisualAccessibilityFinding",
            "VisualAccessibilityStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_for_required_accessibility_rules(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAuthorityAsync",
            "EnsureApprovable",
            "AltText",
            "LongDescription",
            "TextInImageEquivalent",
            "Contrast",
            "ReadingOrder",
            "DecorativeClassification",
            "VisualAccessibilityConflictException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertNotIn("static readonly Dictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("visual_accessibility_history", source)
        self.assertIn("visual_accessibility_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<VisualAccessibilityState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("visual_accessibility_outbox", source)
        self.assertIn("visual_accessibility_history", source)
        self.assertIn("visual_accessibility_receipts", source)

    def test_migration_contains_all_durable_components(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "visual_accessibility_cases",
            "visual_accessibility_assessments",
            "visual_accessibility_findings",
            "visual_accessibility_decisions",
            "visual_accessibility_receipts",
            "visual_accessibility_history",
            "visual_accessibility_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("revision", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("evidence_digest", migration)


if __name__ == "__main__":
    unittest.main()
