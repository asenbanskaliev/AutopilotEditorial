from __future__ import annotations

import json
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
POLICY = ROOT / "docs" / "architecture" / "architecture-policy.json"
ADR = ROOT / "docs" / "architecture" / "ADR-001-clean-architecture-boundaries.md"
SOLUTION = ROOT / "BookStudio.slnx"


def normalize(value: str) -> str:
    return value.replace("\\", "/")


def project_references(path: Path) -> set[str]:
    root = ET.fromstring(path.read_text(encoding="utf-8"))
    return {
        normalize(element.attrib["Include"])
        for element in root.findall(".//ProjectReference")
    }


def package_references(path: Path) -> set[str]:
    root = ET.fromstring(path.read_text(encoding="utf-8"))
    return {
        element.attrib["Include"]
        for element in root.findall(".//PackageReference")
    }


class ArchitecturePolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.assert_path_exists(POLICY)
        cls.policy = json.loads(POLICY.read_text(encoding="utf-8"))
        cls.projects = cls.policy["projects"]
        cls.by_path = {project["projectPath"]: project for project in cls.projects}

    @staticmethod
    def assert_path_exists(path: Path) -> None:
        if not path.exists():
            raise AssertionError(f"Missing architecture contract: {path}")

    def test_policy_and_adr_exist(self) -> None:
        self.assertTrue(POLICY.exists())
        self.assertTrue(ADR.exists())
        self.assertEqual("1.0.0", self.policy["schemaVersion"])

    def test_policy_covers_solution_projects_exactly_once(self) -> None:
        solution = ET.fromstring(SOLUTION.read_text(encoding="utf-8"))
        solution_paths = [
            normalize(element.attrib["Path"])
            for element in solution.findall(".//Project")
        ]
        policy_paths = [project["projectPath"] for project in self.projects]
        self.assertEqual(len(policy_paths), len(set(policy_paths)))
        self.assertEqual(set(solution_paths), set(policy_paths))

    def test_csproj_references_match_policy(self) -> None:
        for project in self.projects:
            project_path = ROOT / project["projectPath"]
            self.assertEqual(
                set(project["allowedProjectReferences"]),
                project_references(project_path),
                project["name"],
            )

    def test_protected_layers_have_no_package_references(self) -> None:
        for project in self.projects:
            if project["packagePolicy"] != "none":
                continue
            packages = package_references(ROOT / project["projectPath"])
            self.assertEqual(set(), packages, project["name"])

    def test_every_project_has_scoped_agent_instructions(self) -> None:
        for project in self.projects:
            agents_path = ROOT / project["agentsPath"]
            self.assertTrue(agents_path.exists(), project["name"])
            content = agents_path.read_text(encoding="utf-8")
            self.assertIn("## Allowed", content, project["name"])
            self.assertIn("## Forbidden", content, project["name"])

    def test_assembly_reference_policy_only_names_known_projects(self) -> None:
        known_names = {project["name"] for project in self.projects}
        for project in self.projects:
            self.assertTrue(
                set(project["allowedBookStudioAssemblyReferences"]).issubset(known_names),
                project["name"],
            )


if __name__ == "__main__":
    unittest.main()
