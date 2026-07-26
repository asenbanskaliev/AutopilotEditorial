from __future__ import annotations

import json
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
POLICY = ROOT / "docs" / "architecture" / "architecture-policy.json"
ADR = ROOT / "docs" / "architecture" / "ADR-001-clean-architecture-boundaries.md"
SOLUTION = ROOT / "BookStudio.slnx"
CENTRAL_PACKAGES = ROOT / "Directory.Packages.props"
USING_PATTERN = re.compile(r"^\s*(?:global\s+)?using\s+([A-Za-z_][A-Za-z0-9_.]*)", re.MULTILINE)


def normalize(value: str) -> str:
    return value.replace("\\", "/")


def project_document(path: Path) -> ET.Element:
    return ET.fromstring(path.read_text(encoding="utf-8"))


def project_references(path: Path) -> set[str]:
    return {
        normalize(element.attrib["Include"])
        for element in project_document(path).findall(".//ProjectReference")
    }


def package_reference_elements(path: Path) -> list[ET.Element]:
    return project_document(path).findall(".//PackageReference")


def source_files(project_path: Path) -> list[Path]:
    return [
        path
        for path in project_path.parent.rglob("*.cs")
        if "bin" not in path.parts and "obj" not in path.parts
    ]


class ArchitecturePolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.assert_path_exists(POLICY)
        cls.policy = json.loads(POLICY.read_text(encoding="utf-8"))
        cls.projects = cls.policy["projects"]
        cls.by_path = {project["projectPath"]: project for project in cls.projects}
        central_document = project_document(CENTRAL_PACKAGES)
        cls.central_packages = {
            element.attrib["Include"]
            for element in central_document.findall(".//PackageVersion")
        }

    @staticmethod
    def assert_path_exists(path: Path) -> None:
        if not path.exists():
            raise AssertionError(f"Missing architecture contract: {path}")

    def test_policy_and_adr_exist(self) -> None:
        self.assertTrue(POLICY.exists())
        self.assertTrue(ADR.exists())
        self.assertEqual("1.0.0", self.policy["schemaVersion"])

    def test_policy_covers_solution_projects_exactly_once(self) -> None:
        solution = project_document(SOLUTION)
        solution_paths = [
            normalize(element.attrib["Path"])
            for element in solution.findall(".//Project")
        ]
        policy_paths = [project["projectPath"] for project in self.projects]
        policy_names = [project["name"] for project in self.projects]
        self.assertEqual(len(policy_paths), len(set(policy_paths)))
        self.assertEqual(len(policy_names), len(set(policy_names)))
        self.assertEqual(set(solution_paths), set(policy_paths))

    def test_csproj_references_match_policy(self) -> None:
        for project in self.projects:
            project_path = ROOT / project["projectPath"]
            self.assertEqual(
                set(project["allowedProjectReferences"]),
                project_references(project_path),
                project["name"],
            )

    def test_package_policies_are_enforced(self) -> None:
        for project in self.projects:
            project_path = ROOT / project["projectPath"]
            packages = package_reference_elements(project_path)
            if project["packagePolicy"] == "none":
                self.assertEqual([], packages, project["name"])
                continue

            for package in packages:
                package_name = package.attrib["Include"]
                self.assertNotIn("Version", package.attrib, project["name"])
                self.assertIn(package_name, self.central_packages, project["name"])

    def test_forbidden_namespace_prefixes_are_absent(self) -> None:
        for project in self.projects:
            prefixes = project["forbiddenNamespacePrefixes"]
            if not prefixes:
                continue

            for path in source_files(ROOT / project["projectPath"]):
                content = path.read_text(encoding="utf-8")
                namespaces = USING_PATTERN.findall(content)
                for namespace in namespaces:
                    forbidden = [
                        prefix
                        for prefix in prefixes
                        if namespace == prefix or namespace.startswith(prefix + ".")
                    ]
                    self.assertEqual([], forbidden, f"{project['name']}:{path}:{namespace}")

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
