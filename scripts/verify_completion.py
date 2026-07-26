from __future__ import annotations

import argparse
import csv
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BACKLOG = ROOT / "docs" / "master-plan" / "full-program-backlog.csv"

ALLOWED = {
    "PLANNED",
    "SPEC_READY",
    "IN_PROGRESS",
    "DUAL_GREEN",
    "AUDITED",
    "VERIFIED",
    "RELEASED",
    "EXCLUDED_BY_CONTRACT",
}
COMPLETE = {"VERIFIED", "RELEASED", "EXCLUDED_BY_CONTRACT"}


def load_rows() -> list[dict[str, str]]:
    with BACKLOG.open(encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle, delimiter=";"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pr-mode", action="store_true")
    parser.add_argument("--full-program", action="store_true")
    args = parser.parse_args()

    rows = load_rows()

    if args.pr_mode:
        invalid = [row["slice_id"] for row in rows if row["status"] not in ALLOWED]
        if invalid:
            raise SystemExit(f"Invalid statuses: {invalid}")
        print("PR governance PASS")
        return

    if args.full_program:
        incomplete = [row["slice_id"] for row in rows if row["status"] not in COMPLETE]
        if incomplete:
            raise SystemExit(
                "FULL_PROGRAM_COMPLETE FAIL. Incomplete slices: " + ", ".join(incomplete)
            )
        print("FULL_PROGRAM_COMPLETE PASS")
        return

    print("No verification mode selected")


if __name__ == "__main__":
    main()
