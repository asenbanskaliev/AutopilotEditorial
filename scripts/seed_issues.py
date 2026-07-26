from __future__ import annotations

import csv
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BACKLOG = ROOT / "docs" / "master-plan" / "full-program-backlog.csv"

LABELS = {
    "type:vertical-slice": "5319e7",
    "status:specification": "d4c5f9",
    "status:ready": "0e8a16",
    "status:blocked": "b60205",
    "method:sdd-dtdd-m": "1d76db",
}


def run(*args: str) -> None:
    subprocess.run(args, check=True)


def ensure_labels() -> None:
    for name, color in LABELS.items():
        run(
            "gh",
            "label",
            "create",
            name,
            "--color",
            color,
            "--force",
        )


def main() -> None:
    ensure_labels()
    with BACKLOG.open(encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle, delimiter=";"))

    for row in rows:
        dependency = row.get("depends_on", "").strip() or "None"
        body = (
            f"## Phase\n{row['phase']}\n\n"
            f"## Objective\n{row['objective']}\n\n"
            f"## Dependency\n{dependency}\n\n"
            "## Methodology\nSDD + Dual TDD + M-Audit\n\n"
            "## Required gates\n"
            "- SPEC_READY\n"
            "- DUAL_RED_CONFIRMED\n"
            "- DUAL_GREEN\n"
            "- NO_ORPHANS_PASS\n"
            "- M_AUDIT_PASS\n"
            "- RETROSPEC_SYNCED\n"
        )
        run(
            "gh",
            "issue",
            "create",
            "--title",
            f"[{row['slice_id']}] {row['title']}",
            "--body",
            body,
            "--label",
            "type:vertical-slice",
            "--label",
            "status:specification",
            "--label",
            "method:sdd-dtdd-m",
        )


if __name__ == "__main__":
    main()
