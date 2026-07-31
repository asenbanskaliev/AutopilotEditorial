from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Authoring/ManuscriptAssemblyContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Authoring/ManuscriptAssemblyOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteManuscriptAssemblyStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0047_manuscript_assembly.sql"
SPEC = ROOT / "docs/specs/VS-110.md"
RED = ROOT / "docs/evidence/VS-110/RED_EVIDENCE.md"


class Vs110ManuscriptAssemblyContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-110 file: {path}")

    def test_contract_exposes_canonical_authority_manifest_and_decisions(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IManuscriptAssemblyStore",
            "IManuscriptAssemblyAuthorityReader",
            "ManuscriptAssemblyDraft",
            "ManuscriptAssemblyAuthority",
            "ManuscriptSourceBinding",
            "ManuscriptSectionDraft",
            "ManuscriptContentNode",
            "ManuscriptCanonicalManifest",
            "ManuscriptAssemblyDecisionCommand",
            "ManuscriptAssemblyStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAuthorityAsync",
            "BuildManifest",
            "EnsureApprovable",
            "OrderBy",
            "ThenBy",
            "ContentDigest",
            "EvidenceDigest",
            "AccessibilityAlternative",
            "ManuscriptAssemblyConflictException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertNotIn("static readonly Dictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("manuscript_assembly_history", source)
        self.assertIn("manuscript_assembly_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<ManuscriptAssemblyState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("manuscript_assembly_outbox", source)
        self.assertIn("manuscript_assembly_history", source)
        self.assertIn("manuscript_assembly_receipts", source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "manuscript_assemblies",
            "manuscript_assembly_sources",
            "manuscript_assembly_sections",
            "manuscript_assembly_nodes",
            "manuscript_assembly_findings",
            "manuscript_assembly_decisions",
            "manuscript_assembly_receipts",
            "manuscript_assembly_history",
            "manuscript_assembly_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("revision", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("manifest_digest", migration)


if __name__ == "__main__":
    unittest.main()
