from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "config" / "ci" / "providers.json"
SCHEMA = ROOT / "schemas" / "ci-evidence.schema.json"
VALIDATOR = ROOT / "scripts" / "validate_ci_providers.py"
LOCAL_RUNNER = ROOT / "scripts" / "run_local_validation.py"


class CiProviderContractTests(unittest.TestCase):
    def test_required_contract_files_exist(self) -> None:
        for path in (CATALOG, SCHEMA, VALIDATOR, LOCAL_RUNNER):
            self.assertTrue(path.exists(), f"Missing CI contract file: {path}")

    def test_catalog_contains_all_provider_types_with_unique_ids(self) -> None:
        data = json.loads(CATALOG.read_text(encoding="utf-8"))
        providers = data["providers"]
        ids = [provider["id"] for provider in providers]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual(
            {"github-hosted", "github-self-hosted", "circleci", "local-evidence"},
            {provider["type"] for provider in providers},
        )

    def test_enabled_provider_priorities_are_unique(self) -> None:
        data = json.loads(CATALOG.read_text(encoding="utf-8"))
        priorities = [
            provider["priority"]
            for provider in data["providers"]
            if provider["enabled"]
        ]
        self.assertEqual(len(priorities), len(set(priorities)))

    def test_skipped_is_not_an_allowed_evidence_result(self) -> None:
        schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
        result_values = schema["properties"]["result"]["enum"]
        self.assertNotIn("SKIPPED", result_values)
        self.assertEqual({"PASS", "FAIL", "BLOCKED"}, set(result_values))


if __name__ == "__main__":
    unittest.main()
