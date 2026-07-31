from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Authoring/AssetRegistryContracts.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteAssetRegistryStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0042_asset_registry.sql"
SPEC = ROOT / "docs/specs/VS-101.md"
RED = ROOT / "docs/evidence/VS-101/RED_EVIDENCE.md"


class Vs101AssetRegistryContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-101 file: {path}")

    def test_sqlite_is_the_durable_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("static readonly Dictionary", source)
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("RequireReceipt(", source)
        self.assertIn("asset_registry_receipts", source)
        self.assertIn("asset_technical_validations", source)

    def test_mutations_are_concurrency_guarded_and_atomic(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("asset_registry_history", source)
        self.assertIn("outbox_messages", source)

    def test_migration_contains_all_durable_components(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "visual_assets",
            "asset_provenance_evidence",
            "asset_rights_evidence",
            "asset_accessibility_evidence",
            "asset_technical_validations",
            "asset_relationships",
            "asset_registry_history",
            "asset_registry_receipts",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("unique", migration)
        self.assertIn("revision", migration)

    def test_contract_exposes_full_lifecycle_and_evidence(self) -> None:
        contract = CONTRACT.read_text(encoding="utf-8")
        for operation in (
            "RegisterAsync",
            "ValidateAsync",
            "DecideAsync",
            "QuarantineAsync",
            "RepairAsync",
            "SupersedeAsync",
            "MarkStaleAsync",
            "GetAsync",
        ):
            self.assertIn(operation, contract)
        for evidence_field in (
            "AssetProvenanceEvidence",
            "AssetRightsEvidence",
            "AssetAccessibilityEvidence",
            "AssetTechnicalValidation",
            "RequestFingerprint",
        ):
            self.assertIn(evidence_field, contract)


if __name__ == "__main__":
    unittest.main()
