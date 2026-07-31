from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Authoring/VisualBriefContracts.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteVisualBriefStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0041_visual_briefs.sql"
SPEC = ROOT / "docs/specs/VS-100.md"
RED = ROOT / "docs/evidence/VS-100/RED_EVIDENCE.md"


class Vs100VisualBriefContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-100 file: {path}")

    def test_sqlite_is_the_durable_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("static readonly Dictionary", source)
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertIn("LoadBrief(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("visual_brief_receipts", source)
        self.assertIn("visual_brief_reviews", source)

    def test_mutations_are_concurrency_guarded_and_atomic(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn(
            "WHEREworkspace_id=$wANDbrief_id=$idANDrevision=$expected",
            normalized,
        )
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("visual_brief_history", source)
        self.assertIn("outbox_messages", source)

    def test_migration_contains_all_durable_components(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "visual_briefs",
            "visual_continuity_references",
            "visual_brief_reviews",
            "visual_brief_history",
            "visual_brief_receipts",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("unique", migration)
        self.assertIn("revision", migration)

    def test_contract_exposes_full_lifecycle_and_evidence(self) -> None:
        contract = CONTRACT.read_text(encoding="utf-8")
        for operation in (
            "CreateAsync",
            "ReviseAsync",
            "ReviewAsync",
            "DecideAsync",
            "MarkStaleAsync",
            "GetAsync",
        ):
            self.assertIn(operation, contract)
        for evidence_field in (
            "AccessibilityIntent",
            "ProhibitedElements",
            "ContinuityReferences",
            "BlockingFindings",
            "RequestFingerprint",
        ):
            self.assertIn(evidence_field, contract)


if __name__ == "__main__":
    unittest.main()
