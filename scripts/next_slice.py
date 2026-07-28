#!/usr/bin/env python3
"""Select the next dependency-ready VS and emit a deterministic scaffold plan.

Fail-closed by design: this script never marks work complete, merges code, or
bypasses gates. It only identifies the next slice whose predecessor is VERIFIED.
"""
from __future__ import annotations

import argparse
import csv
import json
import re
from dataclasses import asdict, dataclass
from pathlib import Path

VS_RE = re.compile(r"^VS-\d{3}$")
COMPLETE = {"VERIFIED", "RELEASED", "EXCLUDED_BY_CONTRACT"}


@dataclass(frozen=True)
class SlicePlan:
    slice_id: str
    phase: str
    title: str
    description: str
    predecessor: str
    branch: str
    issue_title: str


def rows(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8-sig", newline="") as handle:
        return [
            {key: (value or "").strip() for key, value in row.items() if key is not None}
            for row in csv.DictReader(handle, delimiter=";")
        ]


def field(row: dict[str, str], *names: str) -> str:
    lower = {key.lower(): value for key, value in row.items()}
    for name in names:
        if lower.get(name.lower()):
            return lower[name.lower()]
    return ""


def slug(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")[:48] or "slice"


def select(backlog_path: Path, status_path: Path) -> SlicePlan | None:
    backlog = rows(backlog_path)
    status_rows = rows(status_path) if status_path.exists() else []
    statuses: dict[str, str] = {}
    for row in status_rows:
        slice_id = field(row, "slice_id")
        if not VS_RE.fullmatch(slice_id) or slice_id in statuses:
            raise ValueError("Invalid or duplicate slice status row.")
        statuses[slice_id] = field(row, "status")

    seen: set[str] = set()
    ordered = sorted(backlog, key=lambda row: int(field(row, "order") or "0"))
    for row in ordered:
        slice_id = field(row, "slice_id")
        if not VS_RE.fullmatch(slice_id) or slice_id in seen:
            raise ValueError("Invalid or duplicate backlog slice.")
        seen.add(slice_id)
        effective = statuses.get(slice_id, field(row, "status"))
        if effective in COMPLETE:
            continue
        predecessor = field(row, "depends_on", "dependency", "predecessor")
        predecessor_status = statuses.get(predecessor, "") if predecessor else "VERIFIED"
        if predecessor and predecessor_status not in COMPLETE:
            continue
        title = field(row, "title", "name", "nombre") or slice_id
        return SlicePlan(
            slice_id=slice_id,
            phase=field(row, "phase", "fase", "track"),
            title=title,
            description=field(row, "description", "descripcion", "scope"),
            predecessor=predecessor,
            branch=f"agent/{slice_id}-{slug(title)}",
            issue_title=f"{slice_id} — {title}",
        )
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--backlog", default="docs/master-plan/full-program-backlog.csv")
    parser.add_argument("--status", default="docs/execution/SLICE_STATUS.csv")
    parser.add_argument("--github-output")
    args = parser.parse_args()
    plan = select(Path(args.backlog), Path(args.status))
    print(json.dumps({"ready": plan is not None, "plan": asdict(plan) if plan else None}, ensure_ascii=False, sort_keys=True))
    if args.github_output:
        with Path(args.github_output).open("a", encoding="utf-8") as handle:
            handle.write(f"ready={'true' if plan else 'false'}\n")
            if plan:
                for key, value in asdict(plan).items():
                    handle.write(f"{key}={value}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
