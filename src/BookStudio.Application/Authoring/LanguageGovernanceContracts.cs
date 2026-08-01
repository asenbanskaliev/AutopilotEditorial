namespace BookStudio.Application.Authoring;

public interface ILanguageGovernanceStore
{
    ValueTask<LanguagePolicySubmissionResult> SubmitAsync(LanguagePolicyRequest request, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LanguageValidationState> RecordValidationAsync(LanguageValidationCommand command,
        CompiledLanguageContract compiledContract, LanguageValidationResult result,
        DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LanguageValidationState> DecideAsync(LanguageDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LanguageValidationState?> GetAsync(string workspaceId, Guid policyId, CancellationToken ct = default);
}

public interface IProjectLanguageAuthorityReader
{
    ValueTask<ProjectLanguageAuthoritySnapshot> RequireCurrentAsync(ProjectLanguageAuthority authority, CancellationToken ct = default);
}

public interface ILanguageDetector
{
    string DetectorId { get; }
    string DetectorVersion { get; }
    ValueTask<LanguageDetectionResult> DetectAsync(string content, CancellationToken ct = default);
}

public sealed record LanguagePolicyRequest(
    Guid RequestId,
    Guid PolicyId,
    Guid ProjectId,
    string WorkspaceId,
    ProjectLanguageAuthority Authority,
    string UiLanguageTag,
    string BookLanguageTag,
    string LocaleProfile,
    long PolicyRevision,
    IReadOnlyList<AllowedLanguageScope> AllowedSecondaryLanguageScopes,
    string Actor,
    string RequestFingerprint,
    Guid? SupersedesPolicyId = null);

public sealed record ProjectLanguageAuthority(
    Guid ProjectId,
    string WorkspaceId,
    string BookLanguageTag,
    long ProjectRevision,
    string ProjectDigest,
    ProjectLanguageAuthorityStatus Status);

public sealed record ProjectLanguageAuthoritySnapshot(
    ProjectLanguageAuthority Authority,
    bool IsCurrent,
    string AuthorityDigest,
    DateTimeOffset VerifiedAtUtc);

public sealed record AllowedLanguageScope(
    string ScopeId,
    string LanguageTag,
    LanguageScopeKind Kind,
    string LocationPattern,
    string Reason,
    DateTimeOffset? ExpiresAtUtc,
    string ApprovedBy,
    string EvidenceDigest);

public sealed record LanguageInvocationContext(
    Guid InvocationId,
    Guid ProjectId,
    string WorkspaceId,
    Guid PolicyId,
    long PolicyRevision,
    string PolicyDigest,
    string Provider,
    string Model,
    string PromptTemplateVersion,
    string InputDigest,
    string Purpose);

public sealed record CompiledLanguageContract(
    string BookLanguageTag,
    string LocaleProfile,
    string RequiredOutputLanguage,
    string RegionalConventions,
    string SystemInstruction,
    string PolicyDigest,
    string InstructionDigest,
    string ContractVersion);

public sealed record LanguageValidationCommand(
    Guid RequestId,
    Guid PolicyId,
    string WorkspaceId,
    long ExpectedRevision,
    LanguageInvocationContext Invocation,
    string GeneratedText,
    string Actor,
    string RequestFingerprint);

public sealed record LanguageDetectionResult(
    string DetectedLanguageTag,
    decimal Confidence,
    IReadOnlyList<DetectedLanguageSpan> Spans,
    string OutputDigest);

public sealed record DetectedLanguageSpan(
    int Start,
    int Length,
    string LanguageTag,
    decimal Confidence,
    string TextDigest);

public sealed record LanguageFinding(
    string FindingId,
    string RuleId,
    LanguageFindingSeverity Severity,
    int? Start,
    int? Length,
    string ExpectedLanguageTag,
    string DetectedLanguageTag,
    decimal Confidence,
    string Message,
    string EvidenceDigest,
    bool CoveredByApprovedScope);

public sealed record LanguageValidationResult(
    string ExpectedLanguageTag,
    string DetectedLanguageTag,
    decimal Confidence,
    IReadOnlyList<LanguageFinding> Findings,
    bool Accepted,
    bool RetryRequired,
    string DetectorIdentity,
    string InputDigest,
    string OutputDigest,
    string EvidenceDigest);

public sealed record LanguageDecisionCommand(
    Guid RequestId,
    Guid PolicyId,
    string WorkspaceId,
    long ExpectedRevision,
    LanguageDecision Decision,
    string Reason,
    string Evidence,
    string EvidenceDigest,
    string Actor,
    string RequestFingerprint);

public sealed record LanguageValidationState(
    Guid PolicyId,
    Guid ProjectId,
    string WorkspaceId,
    string UiLanguageTag,
    string BookLanguageTag,
    string LocaleProfile,
    long PolicyRevision,
    string PolicyDigest,
    IReadOnlyList<AllowedLanguageScope> AllowedSecondaryLanguageScopes,
    CompiledLanguageContract? CompiledContract,
    LanguageValidationResult? LastValidation,
    LanguageGovernanceStatus Status,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record LanguagePolicySubmissionResult(LanguageValidationState State, bool Replayed);

public enum ProjectLanguageAuthorityStatus { Active, Archived, Superseded }
public enum LanguageScopeKind { Quotation, ProperNoun, Citation, MultilingualPassage, MetadataLiteral }
public enum LanguageFindingSeverity { Info, Warning, Blocking }
public enum LanguageDecision { Approve, Reject, Supersede }
public enum LanguageGovernanceStatus { Draft, Validated, RetryRequired, Approved, Rejected, Superseded }

public sealed class LanguageGovernanceValidationException : Exception
{
    public LanguageGovernanceValidationException(string message) : base(message) { }
}

public sealed class LanguageGovernanceConflictException : Exception
{
    public LanguageGovernanceConflictException(string message) : base(message) { }
}

public sealed class LanguageGovernanceTransitionException : Exception
{
    public LanguageGovernanceTransitionException(string message) : base(message) { }
}
