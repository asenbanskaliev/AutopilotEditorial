from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

REQUIRED_FILES = [
    ROOT / "src/BookStudio.Application/Artifacts/IArtifactStore.cs",
    ROOT / "src/BookStudio.Application/Artifacts/ArtifactManifest.cs",
    ROOT / "src/BookStudio.Application/Artifacts/ArtifactWriteRequest.cs",
    ROOT / "src/BookStudio.Infrastructure/Artifacts/FileSystem/FileArtifactStoreOptions.cs",
    ROOT / "src/BookStudio.Infrastructure/Artifacts/FileSystem/FileArtifactStore.cs",
    ROOT / "src/BookStudio.Infrastructure/Artifacts/FileSystem/ArtifactPathPolicy.cs",
    ROOT / "schemas/artifact-manifest.schema.json",
    ROOT / "docs/architecture/ARTIFACT_STORE_LAYOUT.md",
]


class ArtifactStoreContractTests(unittest.TestCase):
    def test_required_contract_files_exist(self) -> None:
        for path in REQUIRED_FILES:
            self.assertTrue(path.exists(), f"Missing artifact-store contract: {path}")

    def test_manifest_schema_requires_immutable_identity_and_hash_fields(self) -> None:
        schema_path = ROOT / "schemas/artifact-manifest.schema.json"
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        required = set(schema["required"])
        self.assertTrue(
            {"schemaVersion", "artifactId", "version", "sha256", "length", "mediaType", "createdAtUtc"}
            <= required
        )
        self.assertFalse(schema["additionalProperties"])

    def test_layout_document_declares_content_addressed_blobs_and_immutable_manifests(self) -> None:
        content = (ROOT / "docs/architecture/ARTIFACT_STORE_LAYOUT.md").read_text(encoding="utf-8")
        self.assertIn("blobs/sha256", content)
        self.assertIn("manifests/<artifact-id>/<version>.json", content)
        self.assertIn("immutable", content.lower())
        self.assertIn("atomic", content.lower())

    def test_ci_contract_contains_artifact_store_integration(self) -> None:
        catalog = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in catalog["contracts"]}
        self.assertIn("dotnet.artifact-store-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.artifact-store-integration"]["capability"])


if __name__ == "__main__":
    unittest.main()
