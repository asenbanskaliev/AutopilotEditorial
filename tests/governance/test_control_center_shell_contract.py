from __future__ import annotations

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WWWROOT = ROOT / "src/BookStudio.ControlCenter/wwwroot"
REQUIRED = [
    WWWROOT / "index.html",
    WWWROOT / "app.css",
    WWWROOT / "app.js",
]


class ControlCenterShellContractTests(unittest.TestCase):
    def test_required_shell_assets_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing Control Center shell asset: {path}")

    def test_shell_has_semantic_navigation_and_accessibility_contract(self) -> None:
        html = (WWWROOT / "index.html").read_text(encoding="utf-8")
        for token in (
            "<header",
            "<nav",
            "<main",
            "<footer",
            "skip-link",
            "aria-live",
            "data-route=\"/system\"",
            "data-route=\"/configuration\"",
            "data-route=\"/about\"",
        ):
            self.assertIn(token, html)

    def test_shell_uses_only_local_assets_and_no_inline_script(self) -> None:
        html = (WWWROOT / "index.html").read_text(encoding="utf-8")
        self.assertNotRegex(html, r"https?://")
        self.assertNotRegex(html, r"<script(?![^>]+src=)[^>]*>")
        self.assertIn('src="/app.js"', html)
        self.assertIn('href="/app.css"', html)

    def test_javascript_consumes_versioned_safe_apis(self) -> None:
        script = (WWWROOT / "app.js").read_text(encoding="utf-8")
        self.assertIn("/api/v1/diagnostics", script)
        self.assertIn("/api/v1/configuration", script)
        self.assertIn("localStorage", script)
        self.assertIn("popstate", script)
        self.assertIn("aria-current", script)

    def test_css_has_focus_responsive_and_reduced_motion(self) -> None:
        css = (WWWROOT / "app.css").read_text(encoding="utf-8")
        self.assertIn(":focus-visible", css)
        self.assertIn("@media", css)
        self.assertIn("prefers-reduced-motion", css)

    def test_ci_catalog_contains_shell_integration_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.control-center-shell-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.control-center-shell-integration"]["capability"])


if __name__ == "__main__":
    unittest.main()
