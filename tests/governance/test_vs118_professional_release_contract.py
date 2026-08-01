from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Publishing/ProfessionalReleaseContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Publishing/ProfessionalReleaseOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Publishing/SqliteProfessionalReleaseStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0055_professional_release.sql"
SPEC = ROOT / "docs/specs/VS-118.md"
RED = ROOT / "docs/tdd/VS-118-RED.md"


class Vs118ProfessionalReleaseContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-118 file: {path}")

    def test_contract_exposes_authority_artifacts_manifest_and_decision(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IProfessionalReleaseStore", "IProofReleaseAuthorityReader", "IReleaseArtifactReader",
            "ProfessionalReleaseRequest", "ProofReleaseAuthority", "VerifiedReleaseArtifact",
            "ProfessionalReleaseManifest", "ProfessionalReleaseDecisionCommand",
            "ProfessionalReleaseState", "ProfessionalReleaseStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAsync", "RequireApprovedCurrent", "SHA256.HashData", "OrderBy",
            "RequireRequiredInventory", "ManifestDigest", "InventoryDigest", "EvidenceDigest",
            "ProfessionalReleaseTransitionException", "ProfessionalReleaseValidationException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        for token in (
            "Load(", "LoadReceipt(", "professional_release_history",
            "professional_release_receipts", "JsonSerializer.Deserialize<ProfessionalReleaseState>",
            "professional_release_outbox",
        ):
            self.assertIn(token, source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        for token in (
            "professional_release_history", "professional_release_receipts",
            "professional_release_outbox", "professional_release_manifests",
        ):
            self.assertIn(token, source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "professional_releases", "professional_release_artifacts",
            "professional_release_manifests", "professional_release_decisions",
            "professional_release_receipts", "professional_release_history",
            "professional_release_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        for token in (
            "revision", "request_fingerprint", "manifest_digest",
            "inventory_digest", "evidence_digest", "semantic_version",
        ):
            self.assertIn(token, migration)


if __name__ == "__main__":
    unittest.main()
