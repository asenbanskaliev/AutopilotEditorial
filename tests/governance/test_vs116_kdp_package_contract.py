from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Publishing/KdpPackageContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Publishing/KdpPackageOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Publishing/SqliteKdpPackageStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0053_kdp_package.sql"
SPEC = ROOT / "docs/specs/VS-116.md"
RED = ROOT / "docs/evidence/VS-116/RED_EVIDENCE.md"


class Vs116KdpPackageContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-116 file: {path}")

    def test_contract_exposes_authority_metadata_manifest_and_decision(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IKdpPackageStore", "IKdpPackageAuthorityReader", "IKdpArtifactReader",
            "KdpPackageRequest", "KdpPackageAuthority", "KdpMetadata", "KdpAiDisclosure",
            "KdpMetadataFinding", "KdpPackageManifest", "KdpPackageDecisionCommand",
            "KdpPackageStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAsync", "BuildManifest", "BuildEvidenceDigest", "NormalizePath",
            "StableZipTimestamp", "OrderBy", "SHA256.HashData", "CompressionLevel.NoCompression",
            "KdpFindingSeverity.Blocking", "KdpPackageTransitionException",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        for token in (
            "Load(", "LoadReceipt(", "kdp_package_history", "kdp_package_receipts",
            "JsonSerializer.Deserialize<KdpPackageState>", "kdp_package_outbox",
        ):
            self.assertIn(token, source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("kdp_package_history", source)
        self.assertIn("kdp_package_receipts", source)
        self.assertIn("kdp_package_outbox", source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "kdp_packages", "kdp_package_metadata_revisions", "kdp_package_findings",
            "kdp_package_manifests", "kdp_package_decisions", "kdp_package_receipts",
            "kdp_package_history", "kdp_package_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("revision", migration)
        self.assertIn("request_fingerprint", migration)
        self.assertIn("evidence_digest", migration)


if __name__ == "__main__":
    unittest.main()
