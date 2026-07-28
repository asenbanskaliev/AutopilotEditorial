namespace BookStudio.Application.Authoring;

public interface IDiscoverySessionStore
{
    ValueTask<DiscoveryCreateResult> CreateAsync(DiscoverySessionDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<DiscoverySession> AnswerAsync(DiscoveryAnswerCommand command, DateTimeOffset answeredAtUtc, CancellationToken cancellationToken = default);
    ValueTask<DiscoverySession> DecideAsync(DiscoveryDecisionCommand command, DateTimeOffset decidedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<DiscoverySession> SetOpenItemAsync(DiscoveryOpenItemCommand command, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<DiscoveryCompleteResult> CompleteAsync(DiscoveryCompleteCommand command, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<DiscoverySession?> GetAsync(string workspaceId, Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record DiscoverySessionDraft(Guid SessionId, Guid ProjectId, string WorkspaceId, string SchemaVersion, IReadOnlyList<DiscoveryQuestion> Questions, string RequestFingerprint);
public sealed record DiscoveryQuestion(string Key, int Order, DiscoveryQuestionType Type, bool Required, string Prompt);
public sealed record DiscoveryAnswerCommand(Guid RequestId, string WorkspaceId, Guid SessionId, string QuestionKey, string AnswerJson, string Actor, string RequestFingerprint);
public sealed record DiscoveryDecisionCommand(Guid RequestId, string WorkspaceId, Guid SessionId, string DecisionKey, string SelectedOption, string Rationale, string Actor, string? EvidenceReference, string RequestFingerprint);
public sealed record DiscoveryOpenItemCommand(Guid RequestId, string WorkspaceId, Guid SessionId, string ItemKey, string Description, bool Required, bool Resolved, string Actor, string RequestFingerprint);
public sealed record DiscoveryCompleteCommand(Guid RequestId, string WorkspaceId, Guid SessionId, string Actor, string RequestFingerprint);

public sealed record DiscoverySession(Guid SessionId, Guid ProjectId, string WorkspaceId, string SchemaVersion, DiscoverySessionStatus Status, long Version, IReadOnlyList<DiscoveryQuestion> Questions, IReadOnlyList<DiscoveryAnswer> Answers, IReadOnlyList<DiscoveryDecision> Decisions, IReadOnlyList<DiscoveryOpenItem> OpenItems, Guid? CompletionMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record DiscoveryAnswer(string QuestionKey, int Version, string AnswerJson, string Actor, DateTimeOffset AnsweredAtUtc);
public sealed record DiscoveryDecision(string DecisionKey, string SelectedOption, string Rationale, string Actor, string? EvidenceReference, DateTimeOffset DecidedAtUtc);
public sealed record DiscoveryOpenItem(string ItemKey, string Description, bool Required, bool Resolved, string Actor, DateTimeOffset UpdatedAtUtc);
public sealed record DiscoveryCreateResult(DiscoverySession Session, bool Replayed);
public sealed record DiscoveryCompleteResult(DiscoverySession Session, bool Replayed, Guid CompletionMessageId);

public enum DiscoverySessionStatus { Open, Completed, Cancelled }
public enum DiscoveryQuestionType { Text, Choice, MultiChoice, Boolean, Number, Date }

public sealed class DiscoveryConflictException : Exception { public DiscoveryConflictException(string message) : base(message) { } }
public sealed class DiscoveryCompletionException : Exception { public DiscoveryCompletionException(string message) : base(message) { } }
public sealed class DiscoveryImmutableException : Exception { public DiscoveryImmutableException(Guid sessionId) : base($"Discovery session '{sessionId:D}' is immutable.") => SessionId = sessionId; public Guid SessionId { get; } }
