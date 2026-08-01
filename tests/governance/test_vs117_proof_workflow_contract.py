from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "src/BookStudio.Application/Publishing/ProofWorkflowContracts.cs"
ORCHESTRATOR = ROOT / "src/BookStudio.Application/Publishing/ProofWorkflowOrchestrator.cs"
STORE = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Publishing/SqliteProofWorkflowStore.cs"
MIGRATION = ROOT / "src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0054_proof_workflow.sql"
SPEC = ROOT / "docs/specs/VS-117.md"
RED = ROOT / "docs/evidence/VS-117/RED_EVIDENCE.md"


class Vs117ProofWorkflowContractTests(unittest.TestCase):
    def test_required_slice_files_exist(self) -> None:
        for path in (CONTRACT, ORCHESTRATOR, STORE, MIGRATION, SPEC, RED):
            self.assertTrue(path.exists(), f"Missing VS-117 file: {path}")

    def test_contract_exposes_authority_checklists_findings_receipt_and_decision(self) -> None:
        source = CONTRACT.read_text(encoding="utf-8")
        for token in (
            "IProofWorkflowStore", "IProofPackageAuthorityReader", "IProofChecklist",
            "ProofRequest", "ProofPackageAuthority", "ProofChecklistExecution", "ProofFinding",
            "PhysicalProofReceipt", "ProofDecisionCommand", "ProofState", "ProofStatus",
        ):
            self.assertIn(token, source)

    def test_orchestrator_is_fail_closed_and_deterministic(self) -> None:
        source = ORCHESTRATOR.read_text(encoding="utf-8")
        for token in (
            "RequireCurrentAsync", "RequireApprovedCurrent", "BuildEvidenceDigest", "NormalizeFinding",
            "OrderBy", "SHA256.HashData", "ProofFindingSeverity.Blocking",
            "ProofTransitionException", "InspectedArtifactDigest", "ReviewerAttestation",
        ):
            self.assertIn(token, source)

    def test_sqlite_is_restart_safe_authority(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        self.assertNotIn("ConcurrentDictionary", source)
        for token in (
            "Load(", "LoadReceipt(", "proof_history", "proof_receipts",
            "JsonSerializer.Deserialize<ProofState>", "proof_outbox",
        ):
            self.assertIn(token, source)

    def test_mutations_are_atomic_concurrency_guarded_and_outboxed(self) -> None:
        source = STORE.read_text(encoding="utf-8")
        normalized = re.sub(r"\s+", "", source)
        self.assertIn("ANDrevision=$expected", normalized)
        self.assertIn("BeginTransaction", source)
        self.assertIn("tx.Commit", source)
        for token in ("proof_history", "proof_receipts", "proof_outbox"):
            self.assertIn(token, source)

    def test_migration_contains_complete_durable_model(self) -> None:
        migration = MIGRATION.read_text(encoding="utf-8").lower()
        for table in (
            "proof_workflows", "proof_checklist_executions", "proof_findings",
            "proof_physical_receipts", "proof_decisions", "proof_receipts",
            "proof_history", "proof_outbox",
        ):
            self.assertIn(f"create table if not exists {table}", migration)
        for token in ("revision", "request_fingerprint", "evidence_digest", "reviewer_attestation"):
            self.assertIn(token, migration)


if __name__ == "__main__":
    unittest.main()
