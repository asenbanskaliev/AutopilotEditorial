from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Production/DocxRenderContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Production/DocxRenderOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Production/SqliteDocxRenderStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0050_docx_render.sql"
SPEC = ROOT / "docs/specs/VS-113.md"
RED = ROOT / "docs/evidence/VS-113/RED_EVIDENCE.md"


class Vs113DocxContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-113 file: {path}")

    def test_contract_exposes_authority_package_resources_and_decisions(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IDocxRenderStore", "IDocxAuthorityReader", "DocxRenderRequest", "DocxAuthority",
            "DocxPart", "DocxRelationship", "DocxResource", "DocxArtifact",
            "DocxDecisionCommand", "DocxRenderStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAuthorityAsync", "BuildArtifact", "OrderBy", "ThenBy",
            "ArtifactDigest", "ManifestDigest", "AccessibilityAlternative", "DocxConflictException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("docx_render_history", source)
        self.assertIn("docx_render_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<DocxRenderState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("docx_render_outbox", source)
        self.assertIn("docx_render_history", source)
        self.assertIn("docx_render_receipts", source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "docx_renders", "docx_render_parts", "docx_render_relationships", "docx_render_resources",
            "docx_render_findings", "docx_render_decisions", "docx_render_receipts",
            "docx_render_history", "docx_render_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("revision", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("artifact_json", migration)


if __name__ == "__main__":
    unittest.main()
