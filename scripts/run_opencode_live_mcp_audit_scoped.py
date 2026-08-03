#!/usr/bin/env python3
"""Run VS-128 with artifact identifiers scoped to the selected project."""
from __future__ import annotations

import run_opencode_live_mcp_audit as audit


def main() -> int:
    audit.BRIEFING_ID = f"{audit.PROJECT_ID}.draft.briefing"
    audit.OUTLINE_ID = f"{audit.PROJECT_ID}.draft.outline"
    audit.CHAPTER_ID = f"{audit.PROJECT_ID}.draft.chapter-01"
    audit.RELEASE_ARTIFACT_ID = f"{audit.PROJECT_ID}.release.{audit.RELEASE_ID}"
    return audit.main()


if __name__ == "__main__":
    raise SystemExit(main())
