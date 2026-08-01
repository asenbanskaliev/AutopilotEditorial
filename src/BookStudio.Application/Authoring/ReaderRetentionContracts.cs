namespace BookStudio.Application.Authoring;

public interface IReaderRetentionStore
{
    ValueTask<ReaderRetentionSubmissionResult> SubmitAsync(ReaderRetentionRequest request, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ReaderRetentionState> RecordEvaluationAsync(ReaderRetentionEvaluationCommand command, ReaderRetentionEvaluation evaluation, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ReaderRetentionState> PlanRepairAsync(ReaderRetentionRepairCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ReaderRetentionState> DecideAsync(ReaderRetentionDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ReaderRetentionState?> GetAsync(string workspaceId, Guid caseId, CancellationToken ct = default);
}

public sealed record ReaderPromise(
    string Audience,
    string Genre,
    string Locale,
    string ExpectedExperience,
    string EmotionalTrajectory,
    string ReadingLevel,
    decimal MaximumExpositionLoad,
    decimal MinimumHook,
    decimal MinimumConflict,
    decimal MinimumProgression,
    decimal MinimumPayoff,
    IReadOnlyList<string> RequiredGenreConventions,
    IReadOnlyList<string> ProhibitedBetrayals,
    string PromiseDigest);

public sealed record ReaderRetentionRequest(
    Guid RequestId,
    Guid CaseId,
    Guid ProjectId,
    string WorkspaceId,
    long ManuscriptRevision,
    string ManuscriptDigest,
    ReaderPromise Promise,
    string RuleSetVersion,
    IReadOnlyList<string> EvaluatorIdentities,
    string Actor,
    string RequestFingerprint);

public sealed record ReaderRetentionUnit(
    string UnitId,
    ReaderRetentionScope Scope,
    string? ChapterKey,
    string? SceneKey,
    int? Start,
    int? Length,
    string Text,
    string ContentDigest);

public sealed record ReaderRetentionMetric(
    ReaderRetentionDimension Dimension,
    decimal Score,
    decimal Threshold,
    decimal Weight,
    string Evidence,
    string EvidenceDigest);

public sealed record ReaderRetentionFinding(
    string FindingId,
    string RuleId,
    ReaderRetentionSeverity Severity,
    ReaderRetentionScope Scope,
    string UnitId,
    string Message,
    decimal Observed,
    decimal Expected,
    decimal Confidence,
    string EvidenceDigest,
    bool Resolved);

public sealed record ReaderCriticAssessment(
    string CriticId,
    string CriticVersion,
    ReaderCriticRole Role,
    string UnitId,
    decimal AbandonmentProbability,
    IReadOnlyList<ReaderRetentionFinding> Findings,
    string RationaleDigest);

public sealed record ReaderRetentionRiskPoint(
    string UnitId,
    ReaderRetentionRiskBand Band,
    decimal WeightedRisk,
    IReadOnlyList<string> DominantDrivers,
    string EvidenceDigest);

public sealed record ReaderRetentionEvaluationCommand(
    Guid RequestId,
    Guid CaseId,
    string WorkspaceId,
    long ExpectedRevision,
    IReadOnlyList<ReaderRetentionUnit> Units,
    IReadOnlyList<ReaderCriticAssessment> CriticAssessments,
    string Actor,
    string RequestFingerprint);

public sealed record ReaderRetentionEvaluation(
    IReadOnlyList<ReaderRetentionMetric> Metrics,
    IReadOnlyList<ReaderRetentionFinding> Findings,
    IReadOnlyList<ReaderRetentionRiskPoint> RiskMap,
    decimal ManuscriptRisk,
    ReaderRetentionRiskBand ManuscriptBand,
    bool PublicationBlocked,
    string EvaluatorIdentity,
    string EvidenceDigest);

public sealed record ReaderRetentionRepairCommand(
    Guid RequestId,
    Guid CaseId,
    string WorkspaceId,
    long ExpectedRevision,
    string FindingId,
    ReaderRetentionRepairStrategy Strategy,
    ReaderRetentionScope Scope,
    string UnitId,
    int MaximumAttempts,
    string Reason,
    string Actor,
    string RequestFingerprint);

public sealed record ReaderRetentionRepairPlan(
    Guid RepairId,
    string FindingId,
    ReaderRetentionRepairStrategy Strategy,
    ReaderRetentionScope Scope,
    string UnitId,
    int MaximumAttempts,
    ReaderRetentionRepairStatus Status,
    string EvidenceDigest);

public sealed record ReaderRetentionDecisionCommand(
    Guid RequestId,
    Guid CaseId,
    string WorkspaceId,
    long ExpectedRevision,
    ReaderRetentionDecision Decision,
    string Reason,
    string EvidenceDigest,
    string Actor,
    string RequestFingerprint);

public sealed record ReaderRetentionState(
    Guid CaseId,
    Guid ProjectId,
    string WorkspaceId,
    long ManuscriptRevision,
    string ManuscriptDigest,
    ReaderPromise Promise,
    ReaderRetentionEvaluation? LatestEvaluation,
    IReadOnlyList<ReaderRetentionRepairPlan> Repairs,
    ReaderRetentionStatus Status,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReaderRetentionSubmissionResult(ReaderRetentionState State, bool Replayed);

public enum ReaderRetentionDimension { Hook, Desire, Conflict, Novelty, Progression, ExpositionLoad, Tension, Payoff, EmotionalConnection, Clarity, Predictability }
public enum ReaderRetentionScope { Manuscript, Chapter, Scene, Paragraph, Span }
public enum ReaderRetentionSeverity { Info, Minor, Major, Blocking }
public enum ReaderRetentionRiskBand { Low, Medium, High, Critical }
public enum ReaderCriticRole { DevelopmentEditor, GenreSpecialist, ImpatientReader, CharacterCritic, PacingCritic, ContinuityCritic }
public enum ReaderRetentionRepairStrategy { StrengthenHook, CompressExposition, EscalateConflict, RepairPayoff, RepairDialogue, MergeScenes, CutScene, ReorderChapter, ClarifyMotivation, IncreaseNovelty }
public enum ReaderRetentionRepairStatus { Planned, Running, Completed, Failed, Superseded }
public enum ReaderRetentionDecision { Approve, Reject, ReturnToRepair }
public enum ReaderRetentionStatus { Planned, Evaluated, RepairRequired, Approved, Rejected, Stale }
