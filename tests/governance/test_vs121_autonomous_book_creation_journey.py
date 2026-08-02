from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_vs121_autonomous_book_creation_journey_contract() -> None:
    spec = read("docs/specs/VS-121-autonomous-book-creation-journey.md")
    contracts = read("src/BookStudio.Application/Authoring/BookCreationJourneyContracts.cs")
    planner = read("src/BookStudio.Application/Authoring/BookCreationJourneyPlanner.cs")

    for token in (
        "No user is required to execute CLI or MCP commands",
        "GUIDED",
        "SUPERVISED",
        "AUTONOMOUS",
        "bounded repair",
        "restart",
        "RELEASE_READY",
    ):
        assert token.lower() in spec.lower()

    for token in (
        "BookCreationBrief",
        "JourneyAutonomyPolicy",
        "BookCreationJourney",
        "JourneyPhaseProgress",
        "JourneyDecision",
        "JourneyRepairState",
        "JourneyNextAction",
        "IBookCreationJourneyStore",
        "Guided",
        "Supervised",
        "Autonomous",
    ):
        assert token in contracts

    for token in (
        "BookCreationJourneyPlanner",
        "DependenciesApproved",
        "RequiresDecision",
        "MaximumAutomaticRepairAttempts",
        "RequestDecision",
        "Repair",
        "Complete",
        "SHA256",
    ):
        assert token in planner

    canonical = [
        "Intake",
        "EditorialProposal",
        "BookPlan",
        "Authoring",
        "EditorialQuality",
        "ReaderRetention",
        "Visuals",
        "ProductionPackage",
        "Proof",
        "ReleaseReady",
    ]
    positions = [planner.index(f"JourneyPhase.{phase}") for phase in canonical]
    assert positions == sorted(positions)

    assert "Only one blocking user decision" in planner
    assert "Waiting for exact approved upstream authority" in planner
    assert "Repair the smallest safe scope" in planner
    assert "All required phases have exact approved and current authority" in planner
