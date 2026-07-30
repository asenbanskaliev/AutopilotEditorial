namespace BookStudio.Application.Research;

public interface IResearchPlanningStore
{
    ValueTask<ResearchPlanCreateResult> CreateAsync(ResearchPlanDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ResearchPlan> UpdateAsync(ResearchPlanUpdateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ResearchPlan> DecideAsync(ResearchPlanDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ResearchPlan> MarkStaleAsync(ResearchPlanStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ResearchPlan?> GetAsync(string workspaceId, Guid planId, CancellationToken ct = default);
}

public sealed record ResearchPlanDraft(Guid PlanId, Guid ProjectId, string WorkspaceId, Guid OriginalityReviewId, long ExpectedOriginalityRevision, string ExpectedOriginalityDigest, int Version, string Actor, string Evidence, IReadOnlyList<ResearchQuestionDraft> Questions, string RequestFingerprint);
public sealed record ResearchPlanUpdateCommand(Guid RequestId, string WorkspaceId, Guid PlanId, long ExpectedRevision, IReadOnlyList<ResearchQuestionDraft> Questions, string Evidence, string Actor, string RequestFingerprint);
public sealed record ResearchPlanDecisionCommand(Guid RequestId, string WorkspaceId, Guid PlanId, long ExpectedRevision, ResearchPlanDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record ResearchPlanStaleCommand(Guid RequestId, string WorkspaceId, Guid PlanId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record ResearchQuestionDraft(Guid QuestionId, ResearchQuestionType Type, ResearchPriority Priority, string Location, IReadOnlyList<string> ClaimIds, IReadOnlyList<string> EditorialDecisionIds, string Question, string SourceStrategy, string QualityCriteria, string CurrencyCriteria, string CoverageCriteria, string ExpectedEvidence, IReadOnlyList<Guid> DependencyQuestionIds, string? Owner, ResearchQuestionStatus Status, int Attempts);
public sealed record ResearchQuestion(Guid QuestionId, ResearchQuestionType Type, ResearchPriority Priority, string Location, IReadOnlyList<string> ClaimIds, IReadOnlyList<string> EditorialDecisionIds, string Question, string SourceStrategy, string QualityCriteria, string CurrencyCriteria, string CoverageCriteria, string ExpectedEvidence, IReadOnlyList<Guid> DependencyQuestionIds, string? Owner, ResearchQuestionStatus Status, int Attempts);

public sealed record ResearchPlan(Guid PlanId, Guid ProjectId, string WorkspaceId, Guid OriginalityReviewId, long ExpectedOriginalityRevision, string ExpectedOriginalityDigest, int Version, string Actor, string Evidence, long Revision, ResearchPlanStatus Status, IReadOnlyList<ResearchQuestion> Questions, ResearchPlanDecision? Decision, string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ResearchPlanCreateResult(ResearchPlan Plan, bool Replayed);

public enum ResearchPlanStatus { Proposed, Ready, Approved, Blocked, Stale }
public enum ResearchPlanDecision { Approve, Block }
public enum ResearchQuestionType { FactualClaim, HistoricalContext, ScientificTechnical, LegalRegulatory, CulturalSensitivity, Geography, Biography, Terminology, Market, Other }
public enum ResearchPriority { Low, Medium, High, Critical }
public enum ResearchQuestionStatus { Planned, Ready, Blocked, InProgress, Completed, Rejected }

public sealed class ResearchPlanningValidationException : Exception { public ResearchPlanningValidationException(string message) : base(message) { } }
public sealed class ResearchPlanningConflictException : Exception { public ResearchPlanningConflictException(string message) : base(message) { } }
public sealed class ResearchPlanningTransitionException : Exception { public ResearchPlanningTransitionException(string message) : base(message) { } }
