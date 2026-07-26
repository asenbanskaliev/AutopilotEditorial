from __future__ import annotations

import argparse
import csv
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BACKLOG = ROOT / "docs" / "master-plan" / "full-program-backlog.csv"
STATUS = ROOT / "docs" / "execution" / "SLICE_STATUS.csv"

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


def load_csv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle, delimiter=";"))


def load_effective_rows() -> list[dict[str, str]]:
    plan_rows = load_csv(BACKLOG)
    status_rows = load_csv(STATUS) if STATUS.exists() else []
    status_by_id: dict[str, str] = {}
    for row in status_rows:
        slice_id = row["slice_id"]
        if slice_id in status_by_id:
            raise SystemExit(f"Duplicate status row: {slice_id}")
        status_by_id[slice_id] = row["status"]

    known = {row["slice_id"] for row in plan_rows}
    unknown = sorted(set(status_by_id) - known)
    if unknown:
        raise SystemExit("Status overlay references unknown slices: " + ", ".join(unknown))

    result: list[dict[str, str]] = []
    for row in plan_rows:
        effective = dict(row)
        effective["status"] = status_by_id.get(row["slice_id"], row["status"])
        result.append(effective)
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pr-mode", action="store_true")
    parser.add_argument("--full-program", action="store_true")
    args = parser.parse_args()

    rows = load_effective_rows()

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
