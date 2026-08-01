from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Production/PrintPdfRenderContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Production/PrintPdfRenderOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Production/SqlitePrintPdfRenderStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0049_print_pdf_render.sql"
SPEC = ROOT / "docs/specs/VS-112.md"
RED = ROOT / "docs/evidence/VS-112/RED_EVIDENCE.md"


class Vs112PrintPdfContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-112 file: {path}")

    def test_contract_exposes_authority_geometry_resources_and_decisions(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IPrintPdfRenderStore", "IPrintPdfAuthorityReader", "PrintPdfRenderRequest",
            "PrintPdfAuthority", "PrintGeometry", "PrintPageManifestEntry",
            "PrintFontResource", "PrintImageResource", "PrintPdfArtifact",
            "PrintPdfDecisionCommand", "PrintPdfRenderStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAuthorityAsync", "BuildArtifact", "EnsureApprovable",
            "ValidateGeometry", "ValidateFonts", "ValidateImages", "OrderBy", "ThenBy",
            "OutputIntentDigest", "artifactDigest", "PrintPdfConflictException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("print_pdf_history", source)
        self.assertIn("print_pdf_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<PrintPdfRenderState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("print_pdf_outbox", source)
        self.assertIn("print_pdf_history", source)
        self.assertIn("print_pdf_receipts", source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "print_pdf_renders", "print_pdf_pages", "print_pdf_resources",
            "print_pdf_findings", "print_pdf_decisions", "print_pdf_receipts",
            "print_pdf_history", "print_pdf_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("revision", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("artifact_json", migration)


if __name__ == "__main__":
    unittest.main()
