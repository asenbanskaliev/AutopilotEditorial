from __future__ import annotations

import json
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PACKAGE_PROPS = ROOT / "Directory.Packages.props"
INFRA_PROJECT = ROOT / "src" / "BookStudio.Infrastructure" / "BookStudio.Infrastructure.csproj"
SOLUTION = ROOT / "BookStudio.slnx"
POLICY = ROOT / "docs" / "architecture" / "architecture-policy.json"

REQUIRED_FILES = [
    ROOT / "src/BookStudio.Application/Persistence/IWorkspaceDatabaseLifecycle.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/SqliteWorkspaceOptions.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/SqliteConnectionFactory.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/SqliteMigration.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/SqliteMigrationCatalog.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/SqliteMigrationRunner.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/SqliteWriteQueue.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/SqliteWorkspaceDatabase.cs",
    ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0001_workspace_metadata.sql",
    ROOT / "tests/BookStudio.Tests.Integration/BookStudio.Tests.Integration.csproj",
    ROOT / "tests/BookStudio.Tests.Integration/Program.cs",
    ROOT / "tests/BookStudio.Tests.Integration/AGENTS.md",
]


def xml_root(path: Path) -> ET.Element:
    return ET.fromstring(path.read_text(encoding="utf-8"))


class SqlitePersistenceContractTests(unittest.TestCase):
    def test_required_sqlite_contract_files_exist(self) -> None:
        for path in REQUIRED_FILES:
            self.assertTrue(path.exists(), f"Missing SQLite contract file: {path}")

    def test_sqlite_packages_are_centrally_pinned_to_secure_versions(self) -> None:
        package_versions = {
            element.attrib["Include"]: element.attrib["Version"]
            for element in xml_root(PACKAGE_PROPS).findall(".//PackageVersion")
        }
        self.assertEqual("10.0.10", package_versions["Microsoft.Data.Sqlite"])
        self.assertEqual("2.1.12", package_versions["SQLitePCLRaw.bundle_e_sqlite3"])
        self.assertEqual("2.1.12", package_versions["SQLitePCLRaw.lib.e_sqlite3"])

    def test_infrastructure_references_sqlite_without_inline_version(self) -> None:
        packages = xml_root(INFRA_PROJECT).findall(".//PackageReference")
        matching = [
            package
            for package in packages
            if package.attrib.get("Include") == "Microsoft.Data.Sqlite"
        ]
        self.assertEqual(1, len(matching))
        self.assertNotIn("Version", matching[0].attrib)

    def test_integration_project_is_in_solution_and_architecture_policy(self) -> None:
        integration_path = "tests/BookStudio.Tests.Integration/BookStudio.Tests.Integration.csproj"
        solution_paths = {
            element.attrib["Path"].replace("\\", "/")
            for element in xml_root(SOLUTION).findall(".//Project")
        }
        self.assertIn(integration_path, solution_paths)

        policy = json.loads(POLICY.read_text(encoding="utf-8"))
        by_path = {project["projectPath"]: project for project in policy["projects"]}
        self.assertIn(integration_path, by_path)
        self.assertEqual("integration-test", by_path[integration_path]["layer"])

    def test_initial_migration_is_embedded_and_defines_generic_metadata_only(self) -> None:
        resources = {
            element.attrib["Include"].replace("\\", "/")
            for element in xml_root(INFRA_PROJECT).findall(".//EmbeddedResource")
        }
        self.assertIn(
            "Persistence/Sqlite/Migrations/*.sql",
            resources,
        )

        migration = REQUIRED_FILES[8].read_text(encoding="utf-8").lower()
        self.assertIn("create table", migration)
        self.assertIn("workspace_metadata", migration)
        for forbidden in ("chapter", "artifact", "outbox", "autopilot_job", "book_project"):
            self.assertNotIn(forbidden, migration)

    def test_ci_catalog_contains_sqlite_integration_contract(self) -> None:
        catalog = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {contract["id"]: contract for contract in catalog["contracts"]}
        self.assertIn("dotnet.sqlite-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.sqlite-integration"]["capability"])


if __name__ == "__main__":
    unittest.main()
