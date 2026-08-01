from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Production/EpubRenderContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Production/EpubRenderOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Production/SqliteEpubRenderStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0048_epub_render.sql"
SPEC = ROOT / "docs/specs/VS-111.md"
RED = ROOT / "docs/evidence/VS-111/RED_EVIDENCE.md"


class Vs111EpubRendererContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-111 file: {path}")

    def test_contract_exposes_authority_package_validation_and_decisions(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IEpubRenderStore",
            "IEpubManuscriptAuthorityReader",
            "EpubRenderRequest",
            "EpubManuscriptAuthority",
            "EpubPackage",
            "EpubPackageEntry",
            "EpubValidationCommand",
            "EpubDecisionCommand",
            "EpubRenderStatus",
        ):
            self.assertIn(token, source)
        self.assertIn("SubmitAsync(EpubRenderRequest request, EpubPackage package", source)

    def test_orchestrator_materializes_deterministic_package_before_persistence(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentApprovedAsync",
            "BuildPackage",
            "var package = BuildPackage(request, snapshot)",
            "SubmitAsync(request, package",
            "mimetype",
            "EpubCompression.Stored",
            "OEBPS/nav.xhtml",
            "OEBPS/package.opf",
            "OrderBy",
            "ThenBy",
            "AccessibilityAlternative",
        ):
            self.assertIn(token, source)

    def test_sqlite_persists_rendered_package_entries_and_restart_state(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertIn("EpubRenderStatus.Rendered", source)
        self.assertIn("PersistEntries", source)
        self.assertIn("epub_render_entries", source)
        self.assertIn("epub_render_history", source)
        self.assertIn("epub_render_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<EpubRenderState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("epub_render_outbox", source)
        self.assertIn("epub_render_history", source)
        self.assertIn("epub_render_receipts", source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "epub_renders",
            "epub_render_entries",
            "epub_render_findings",
            "epub_render_decisions",
            "epub_render_receipts",
            "epub_render_history",
            "epub_render_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("entry_order", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("revision", migration)


if __name__ == "__main__":
    unittest.main()
