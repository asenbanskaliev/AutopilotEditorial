from __future__ import annotations

import csv
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BACKLOG = ROOT / "docs" / "master-plan" / "full-program-backlog.csv"
COMPLETE = {"VERIFIED", "RELEASED", "EXCLUDED_BY_CONTRACT"}

with BACKLOG.open(encoding="utf-8-sig", newline="") as handle:
    rows = list(csv.DictReader(handle, delimiter=";"))

by_id = {row["slice_id"]: row for row in rows}

for row in sorted(rows, key=lambda item: int(item["order"])):
    if row["status"] != "PLANNED":
        continue
    dependency = row.get("depends_on", "").strip()
    if not dependency or by_id[dependency]["status"] in COMPLETE:
        print(json.dumps(row, ensure_ascii=False, indent=2))
        break
else:
    print("No READY slice. Check blockers or completion status.")
