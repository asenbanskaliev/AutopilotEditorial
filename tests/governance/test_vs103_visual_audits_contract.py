from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Authoring/VisualAuditContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Authoring/VisualAuditOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteVisualAuditStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0044_visual_audits.sql"
SPEC = ROOT / "docs/specs/VS-103.md"
RED = ROOT / "docs/evidence/VS-103/RED_EVIDENCE.md"


class Vs103VisualAuditsContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-103 file: {path}")

    def test_contract_is_provider_neutral_and_complete(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IVisualAuditCheckProvider",
            "IVisualAuditPolicyCatalog",
            "IVisualAuditStore",
            "VisualAuditCheckResult",
            "VisualAuditFinding",
            "VisualAuditWaiver",
            "VisualAuditDecision",
            "HumanReviewRequired",
            "NonWaivableFindings",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "ValidatePolicy",
            "ResolveProvider",
            "ValidateCheck",
            "IncompleteCoverage",
            "NonWaivableFindings",
            "MinimumSemanticConfidence",
            "CompleteAsync",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertNotIn("static readonly Dictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("JsonSerializer.Deserialize<VisualAuditState>", source)
        self.assertIn("RequireAuthorities", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("visual_audit_history", source)
        self.assertIn("visual_audit_receipts", source)
        self.assertIn("outbox_messages", source)
        self.assertIn("image_adapter_requests", source)

    def test_migration_contains_full_durable_lifecycle(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "visual_audits",
            "visual_audit_checks",
            "visual_audit_findings",
            "visual_audit_decisions",
            "visual_audit_waivers",
            "visual_audit_receipts",
            "visual_audit_history",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("expected_asset_digest", migration)
        self.assertIn("expected_visual_brief_digest", migration)
        self.assertIn("evidence_digest", migration)
        self.assertIn("unique", migration)


if __name__ == "__main__":
    unittest.main()
