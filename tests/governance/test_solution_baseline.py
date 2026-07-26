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
POLICY = ROOT / "docs" / "architecture" / "architecture-policy.json"


def normalize(value: str) -> str:
    return value.replace("\\", "/")


def project_references(path: Path) -> set[str]:
    root = ET.fromstring(path.read_text(encoding="utf-8"))
    return {
        normalize(element.attrib["Include"])
        for element in root.findall(".//ProjectReference")
    }


class SolutionBaselineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.policy = json.loads(POLICY.read_text(encoding="utf-8"))
        cls.projects = cls.policy["projects"]

    def test_required_root_build_files_exist(self) -> None:
        for path in (SOLUTION, GLOBAL_JSON, BUILD_PROPS, PACKAGE_PROPS, POLICY):
            self.assertTrue(path.exists(), f"Missing root build file: {path}")

    def test_global_json_pins_dotnet_10_sdk(self) -> None:
        data = json.loads(GLOBAL_JSON.read_text(encoding="utf-8"))
        version = data["sdk"]["version"]
        self.assertRegex(version, r"^10\.0\.\d+$")
        self.assertEqual("latestPatch", data["sdk"]["rollForward"])
        self.assertFalse(data["sdk"]["allowPrerelease"])

    def test_all_required_projects_exist_with_policy_references(self) -> None:
        for definition in self.projects:
            relative_path = definition["projectPath"]
            project = ROOT / relative_path
            self.assertTrue(project.exists(), f"Missing project: {relative_path}")
            self.assertEqual(
                set(definition["allowedProjectReferences"]),
                project_references(project),
                f"Unexpected references in {relative_path}",
            )

    def test_solution_contains_each_policy_project_once(self) -> None:
        tree = ET.fromstring(SOLUTION.read_text(encoding="utf-8"))
        solution_paths = [
            normalize(element.attrib["Path"])
            for element in tree.findall(".//Project")
        ]
        policy_paths = [definition["projectPath"] for definition in self.projects]
        self.assertEqual(len(policy_paths), len(solution_paths))
        self.assertEqual(set(policy_paths), set(solution_paths))

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
