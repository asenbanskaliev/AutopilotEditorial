from __future__ import annotations

import csv
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BACKLOG = ROOT / "docs" / "master-plan" / "full-program-backlog.csv"
STATUS = ROOT / "docs" / "execution" / "SLICE_STATUS.csv"
WAVE_PLAN = ROOT / "docs" / "execution" / "WAVE_PLAN.md"


class BacklogGovernanceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        with BACKLOG.open(encoding="utf-8-sig", newline="") as handle:
            cls.rows = list(csv.DictReader(handle, delimiter=";"))
        cls.by_id = {row["slice_id"]: row for row in cls.rows}

        with STATUS.open(encoding="utf-8-sig", newline="") as handle:
            cls.status_rows = list(csv.DictReader(handle, delimiter=";"))
        cls.status_by_id = {row["slice_id"]: row for row in cls.status_rows}

    def test_program_contains_104_unique_slices(self) -> None:
        ids = [row["slice_id"] for row in self.rows]
        self.assertEqual(104, len(ids))
        self.assertEqual(104, len(set(ids)))

    def test_all_dependencies_reference_known_slices(self) -> None:
        known = set(self.by_id)
        for row in self.rows:
            dependency = row.get("depends_on", "").strip()
            if dependency:
                self.assertIn(dependency, known, row["slice_id"])

    def test_status_overlay_references_known_slices(self) -> None:
        for slice_id in self.status_by_id:
            self.assertIn(slice_id, self.by_id)

    def test_bootstrap_and_backlog_slice_are_verified(self) -> None:
        self.assertEqual("VERIFIED", self.status_by_id["VS-000"]["status"])
        self.assertEqual("VERIFIED", self.status_by_id["VS-001"]["status"])

    def test_wave_plan_covers_every_program_phase(self) -> None:
        self.assertTrue(WAVE_PLAN.exists(), "WAVE_PLAN.md has not been created")
        content = WAVE_PLAN.read_text(encoding="utf-8")
        phases = {row["phase"] for row in self.rows}
        for phase in phases:
            self.assertIn(phase, content)


if __name__ == "__main__":
    unittest.main()
