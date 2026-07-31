from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Authoring/CoverWorkflowContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Authoring/CoverWorkflowOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteCoverWorkflowStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0045_cover_workflow.sql"
SPEC = ROOT / "docs/specs/VS-104.md"
RED = ROOT / "docs/evidence/VS-104/RED_EVIDENCE.md"


class Vs104CoverWorkflowContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-104 file: {path}")

    def test_contract_exposes_authority_geometry_typography_and_decisions(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "ICoverWorkflowStore", "ICoverAuthorityReader", "CoverAuthorityReference",
            "CoverGeometry", "CoverTypography", "CoverPlacement", "CoverValidationEvidence",
            "Select", "Approve", "ReturnToRepair", "Reject", "Supersede",
        ):
            self.assertIn(token, source)

    def test_orchestrator_fails_closed_on_authority_geometry_and_coverage(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAsync", "ValidateVariant", "EnsureRequiredCoverage",
            "ThumbnailLegibility", "Barcode", "Spine", "LineageEvidenceDigest",
            "CoverWorkflowConflictException", "CoverWorkflowTransitionException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertNotIn("static readonly Dictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("JsonSerializer.Deserialize<CoverProjectState>", source)
        self.assertIn("RequireAuthorities", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("cover_workflow_history", source)
        self.assertIn("cover_workflow_receipts", source)
        self.assertIn("outbox_messages", source)

    def test_migration_contains_complete_durable_cover_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "cover_projects", "cover_variants", "cover_placements", "cover_validations",
            "cover_decisions", "cover_workflow_receipts", "cover_workflow_history",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("selected_variant_id", migration)
        self.assertIn("artifact_digest", migration)
        self.assertIn("lineage_evidence_digest", migration)
        self.assertIn("unique", migration)


if __name__ == "__main__":
    unittest.main()
