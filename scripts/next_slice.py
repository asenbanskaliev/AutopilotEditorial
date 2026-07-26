from __future__ import annotations

import csv
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BACKLOG = ROOT / "docs" / "master-plan" / "full-program-backlog.csv"
STATUS = ROOT / "docs" / "execution" / "SLICE_STATUS.csv"
COMPLETE = {"VERIFIED", "RELEASED", "EXCLUDED_BY_CONTRACT"}


def load_csv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle, delimiter=";"))


rows = load_csv(BACKLOG)
status_rows = load_csv(STATUS) if STATUS.exists() else []
status_by_id = {row["slice_id"]: row["status"] for row in status_rows}
by_id = {row["slice_id"]: row for row in rows}


def effective_status(slice_id: str) -> str:
    return status_by_id.get(slice_id, by_id[slice_id]["status"])


for row in sorted(rows, key=lambda item: int(item["order"])):
    if effective_status(row["slice_id"]) != "PLANNED":
        continue
    dependency = row.get("depends_on", "").strip()
    if not dependency or effective_status(dependency) in COMPLETE:
        output = dict(row)
        output["effective_status"] = effective_status(row["slice_id"])
        print(json.dumps(output, ensure_ascii=False, indent=2))
        break
else:
    print("No READY slice. Check blockers or completion status.")
