namespace BookStudio.Application.Authoring;

public interface IBookCreationJourneyStore
{
    ValueTask<BookCreationJourneyCreateResult> CreateAsync(BookCreationJourneyDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<BookCreationJourney> ApplyAsync(BookCreationJourneyCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<BookCreationJourney?> GetAsync(string workspaceId, Guid journeyId, CancellationToken ct = default);
}

public sealed record BookCreationBrief(
    string Idea,
    string Audience,
    string Genre,
    string BookLanguageTag,
    int TargetWordCount,
    string Tone,
    bool ImagesRequired,
    IReadOnlySet<string> OutputFormats,
    decimal? MaximumCost,
    string? CostCurrency,
    string UserLanguageTag);

public sealed record JourneyAutonomyPolicy(
    JourneyAutonomyMode Mode,
    int MaximumAutomaticRepairAttempts,
    decimal? MaximumAutomaticRepairCost,
    bool RequirePlanApproval,
    bool RequireCoverApproval,
    bool RequireManuscriptApproval,
    bool RequirePhysicalProof,
    IReadOnlySet<JourneyDecisionKind> AlwaysEscalate);

public sealed record BookCreationJourneyDraft(
    Guid JourneyId,
    Guid ProjectId,
    string WorkspaceId,
    BookCreationBrief Brief,
    JourneyAutonomyPolicy Autonomy,
    string Actor,
    string RequestFingerprint);

public sealed record JourneyAuthorityReference(
    JourneyPhase Phase,
    string AuthorityType,
    Guid AuthorityId,
    long AuthorityRevision,
    string AuthorityDigest,
    bool Approved,
    bool Current);

public sealed record JourneyPhaseProgress(
    JourneyPhase Phase,
    JourneyPhaseStatus Status,
    int CompletedUnits,
    int TotalUnits,
    string UserFacingSummary,
    JourneyAuthorityReference? Authority,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record JourneyDecisionOption(string OptionId, string Label, string Consequence, bool Recommended);

public sealed record JourneyDecision(
    Guid DecisionId,
    JourneyDecisionKind Kind,
    JourneyPhase Phase,
    string Title,
    string Explanation,
    IReadOnlyList<JourneyDecisionOption> Options,
    string? RecommendedOptionId,
    JourneyDecisionStatus Status,
    string? SelectedOptionId,
    string EvidenceDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ResolvedAtUtc);

public sealed record JourneyRepairState(
    JourneyPhase Phase,
    string Scope,
    int Attempt,
    int MaximumAttempts,
    string FindingDigest,
    JourneyRepairStatus Status,
    string? EscalationReason);

public sealed record JourneyNextAction(
    JourneyActionKind Kind,
    JourneyPhase Phase,
    string Reason,
    bool Automatic,
    string ActionDigest,
    Guid? DecisionId = null);

public sealed record BookCreationJourney(
    Guid JourneyId,
    Guid ProjectId,
    string WorkspaceId,
    BookCreationBrief Brief,
    JourneyAutonomyPolicy Autonomy,
    JourneyStatus Status,
    JourneyPhase CurrentPhase,
    IReadOnlyList<JourneyPhaseProgress> Progress,
    IReadOnlyList<JourneyDecision> Decisions,
    IReadOnlyList<JourneyRepairState> Repairs,
    JourneyNextAction NextAction,
    long Revision,
    Guid? OutboxMessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record BookCreationJourneyCreateResult(BookCreationJourney Journey, bool Replayed);

public sealed record BookCreationJourneyCommand(
    Guid RequestId,
    Guid JourneyId,
    string WorkspaceId,
    long ExpectedRevision,
    JourneyCommandKind Kind,
    JourneyAuthorityReference? Authority,
    Guid? DecisionId,
    string? SelectedOptionId,
    string? RepairScope,
    string Actor,
    string RequestFingerprint);

public enum JourneyAutonomyMode { Guided, Supervised, Autonomous }
public enum JourneyStatus { Active, WaitingForDecision, Paused, Completed, Cancelled, Failed }
public enum JourneyPhase { Intake, EditorialProposal, BookPlan, Authoring, EditorialQuality, ReaderRetention, Visuals, ProductionPackage, Proof, ReleaseReady }
public enum JourneyPhaseStatus { Pending, Ready, Running, Repairing, WaitingForDecision, Approved, Skipped, Failed }
public enum JourneyDecisionKind { CreativeChoice, PlanApproval, CoverApproval, ManuscriptApproval, LegalRisk, SafetyRisk, BudgetBreach, RetryExhausted, PhysicalProof }
public enum JourneyDecisionStatus { Open, Resolved, Superseded }
public enum JourneyRepairStatus { Planned, Running, Accepted, Exhausted }
public enum JourneyActionKind { StartPhase, ContinuePhase, Repair, RequestDecision, Pause, Complete, None }
public enum JourneyCommandKind { Advance, RecordAuthority, OpenDecision, ResolveDecision, StartRepair, CompleteRepair, Pause, Resume, Cancel }

public sealed class BookCreationJourneyValidationException : Exception
{
    public BookCreationJourneyValidationException(string message) : base(message) { }
}

public sealed class BookCreationJourneyConflictException : Exception
{
    public BookCreationJourneyConflictException(string message) : base(message) { }
}