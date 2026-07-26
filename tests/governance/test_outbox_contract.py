from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

REQUIRED = [
    ROOT / "src/BookStudio.Domain/Events/IDomainEvent.cs",
    ROOT / "src/BookStudio.Application/Outbox/IOutboxStore.cs",
    ROOT / "src/BookStudio.Application/Outbox/OutboxMessage.cs",
    ROOT / "src/BookStudio.Application/Outbox/OutboxMessageDraft.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Outbox/SqliteOutboxStore.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0002_outbox.sql",
    ROOT / "tests/BookStudio.Tests.Integration/OutboxJourney.cs",
]


class OutboxContractTests(unittest.TestCase):
    def test_required_outbox_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing outbox contract: {path}")

    def test_outbox_migration_declares_lease_and_retry_fields(self) -> None:
        sql = REQUIRED[5].read_text(encoding="utf-8").lower()
        for token in (
            "outbox_messages",
            "message_id",
            "payload_json",
            "status",
            "attempts",
            "locked_by",
            "locked_until_utc",
            "available_at_utc",
            "last_error",
            "processed_at_utc",
        ):
            self.assertIn(token, sql)
        self.assertIn("check", sql)
        self.assertIn("create index", sql)

    def test_ci_catalog_contains_outbox_integration_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.outbox-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.outbox-integration"]["capability"])


if __name__ == "__main__":
    unittest.main()
