from __future__ import annotations

import csv
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BACKLOG = ROOT / "docs" / "master-plan" / "full-program-backlog.csv"

REQUIRED_GATES = {
    "SPEC_READY",
    "DUAL_RED_CONFIRMED",
    "DUAL_GREEN",
    "NO_ORPHANS_PASS",
    "M_AUDIT_PASS",
    "RETROSPEC_SYNCED",
}


def fail(message: str) -> None:
    print(f"ERROR: {message}")
    raise SystemExit(1)


def load_rows() -> list[dict[str, str]]:
    if not BACKLOG.exists():
        fail(f"Missing backlog: {BACKLOG}")
    with BACKLOG.open(encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle, delimiter=";"))


def main() -> None:
    rows = load_rows()
    ids = [row["slice_id"] for row in rows]
    if len(ids) != len(set(ids)):
        fail("Duplicate slice IDs")

    known = set(ids)
    previous_order = 0
    for row in rows:
        order = int(row["order"])
        if order <= previous_order:
            fail(f"Invalid order at {row['slice_id']}")
        previous_order = order

        dependency = row.get("depends_on", "").strip()
        if dependency and dependency not in known:
            fail(f"{row['slice_id']} depends on unknown slice {dependency}")

        if row["status"] not in {
            "PLANNED",
            "SPEC_READY",
            "IN_PROGRESS",
            "DUAL_GREEN",
            "AUDITED",
            "VERIFIED",
            "RELEASED",
            "EXCLUDED_BY_CONTRACT",
        }:
            fail(f"Invalid status in {row['slice_id']}: {row['status']}")

    print(f"Plan integrity PASS: {len(rows)} vertical slices")
    print("Methodology contract: SDD-DTDD-M")
    print("Required gates: " + ", ".join(sorted(REQUIRED_GATES)))


if __name__ == "__main__":
    main()
