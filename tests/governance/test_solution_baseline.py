from __future__ import annotations

import json
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOLUTION = ROOT / "BookStudio.slnx"
GLOBAL_JSON = ROOT / "global.json"
BUILD_PROPS = ROOT / "Directory.Build.props"
PACKAGE_PROPS = ROOT / "Directory.Packages.props"

PROJECTS = {
    "src/BookStudio.Domain/BookStudio.Domain.csproj": set(),
    "src/BookStudio.Application/BookStudio.Application.csproj": {
        "../BookStudio.Domain/BookStudio.Domain.csproj"
    },
    "src/BookStudio.Infrastructure/BookStudio.Infrastructure.csproj": {
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Domain/BookStudio.Domain.csproj",
    },
    "src/BookStudio.Mcp/BookStudio.Mcp.csproj": {
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Infrastructure/BookStudio.Infrastructure.csproj",
    },
    "src/BookStudio.OpenCode/BookStudio.OpenCode.csproj": {
        "../BookStudio.Application/BookStudio.Application.csproj"
    },
    "src/BookStudio.Autopilot/BookStudio.Autopilot.csproj": {
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Domain/BookStudio.Domain.csproj",
    },
    "src/BookStudio.Worker/BookStudio.Worker.csproj": {
        "../BookStudio.Autopilot/BookStudio.Autopilot.csproj",
        "../BookStudio.Infrastructure/BookStudio.Infrastructure.csproj",
        "../BookStudio.OpenCode/BookStudio.OpenCode.csproj",
    },
    "src/BookStudio.ControlCenter/BookStudio.ControlCenter.csproj": {
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Infrastructure/BookStudio.Infrastructure.csproj",
    },
    "tests/BookStudio.Tests.Architecture/BookStudio.Tests.Architecture.csproj": set(),
}


def project_references(path: Path) -> set[str]:
    root = ET.fromstring(path.read_text(encoding="utf-8"))
    return {
        element.attrib["Include"].replace("\\", "/")
        for element in root.findall(".//ProjectReference")
    }


class SolutionBaselineTests(unittest.TestCase):
    def test_required_root_build_files_exist(self) -> None:
        for path in (SOLUTION, GLOBAL_JSON, BUILD_PROPS, PACKAGE_PROPS):
            self.assertTrue(path.exists(), f"Missing root build file: {path}")

    def test_global_json_pins_dotnet_10_sdk(self) -> None:
        data = json.loads(GLOBAL_JSON.read_text(encoding="utf-8"))
        version = data["sdk"]["version"]
        self.assertRegex(version, r"^10\.0\.\d+$")
        self.assertEqual("latestPatch", data["sdk"]["rollForward"])
        self.assertFalse(data["sdk"]["allowPrerelease"])

    def test_all_required_projects_exist_with_exact_references(self) -> None:
        for relative_path, expected_references in PROJECTS.items():
            project = ROOT / relative_path
            self.assertTrue(project.exists(), f"Missing project: {relative_path}")
            self.assertEqual(
                expected_references,
                project_references(project),
                f"Unexpected references in {relative_path}",
            )

    def test_solution_contains_each_project_once(self) -> None:
        tree = ET.fromstring(SOLUTION.read_text(encoding="utf-8"))
        paths = [
            element.attrib["Path"].replace("\\", "/")
            for element in tree.findall(".//Project")
        ]
        self.assertEqual(len(PROJECTS), len(paths))
        self.assertEqual(set(PROJECTS), set(paths))

    def test_directory_build_props_enforces_net10_and_quality(self) -> None:
        tree = ET.fromstring(BUILD_PROPS.read_text(encoding="utf-8"))
        values = {element.tag: element.text for element in tree.findall(".//PropertyGroup/*")}
        self.assertEqual("net10.0", values["TargetFramework"])
        self.assertEqual("enable", values["Nullable"])
        self.assertEqual("enable", values["ImplicitUsings"])
        self.assertEqual("true", values["Deterministic"])
        self.assertEqual("true", values["TreatWarningsAsErrors"])
        self.assertEqual("true", values["ManagePackageVersionsCentrally"])


if __name__ == "__main__":
    unittest.main()
