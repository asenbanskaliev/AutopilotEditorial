from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Authoring/ImageAdapterContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Authoring/ImageAdapterOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteImageAdapterRequestStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0043_image_adapters.sql"
SPEC = ROOT / "docs/specs/VS-102.md"
RED = ROOT / "docs/evidence/VS-102/RED_EVIDENCE.md"


class Vs102ImageAdaptersContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-102 file: {path}")

    def test_provider_neutral_contract_and_supported_adapter_classes(self) -> None:
        contract = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IImageAdapter",
            "IImageAdapterRegistry",
            "IImageAdapterRequestStore",
            "ComfyUi",
            "LocalEngine",
            "RemoteProvider",
            "ManualIngestion",
            "ImageAdapterCapabilities",
            "ImageAdapterAttemptResult",
            "ImageAdapterUsage",
            "ImageAdapterError",
        ):
            self.assertIn(token, contract)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        self.assertNotIn("static readonly Dictionary", source)
        self.assertIn("Load(", source)
        self.assertIn("LoadReceipt(", source)
        self.assertIn("image_adapter_history", source)
        self.assertIn("image_adapter_receipts", source)
        self.assertIn("JsonSerializer.Deserialize<ImageAdapterRequestState>", source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        self.assertIn("outbox_messages", source)
        self.assertIn("RequireBriefAuthority", source)
        self.assertIn("status!=\"APPROVED\"", normalized)

    def test_orchestrator_enforces_capabilities_retry_validation_and_registry(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "ValidateAdapter",
            "MaximumAttempts",
            "RetryableFailures",
            "ValidateOutput",
            "RegisterAsync",
            "CompleteAsync",
            "CancelAsync",
            "Path.IsPathRooted",
            "ContentDigest",
        ):
            self.assertIn(token, source)

    def test_migration_contains_durable_request_attempt_output_receipt_and_history(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "image_adapter_requests",
            "image_adapter_attempts",
            "image_adapter_outputs",
            "image_adapter_receipts",
            "image_adapter_history",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        self.assertIn("expected_visual_brief_digest", migration)
        self.assertIn("provider_evidence_digest", migration)
        self.assertIn("canonical_storage_identity", migration)
        self.assertIn("unique", migration)


if __name__ == "__main__":
    unittest.main()
