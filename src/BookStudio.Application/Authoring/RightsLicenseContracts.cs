namespace BookStudio.Application.Authoring;

public interface IRightsLicenseStore
{
    ValueTask<RightsLicenseCreateResult> CreateAsync(RightsLicenseDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RightsLicenseCase> ValidateAsync(RightsLicenseValidateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RightsLicenseCase> DecideAsync(RightsLicenseDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RightsLicenseCase> ReopenAsync(RightsLicenseReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RightsLicenseCase> MarkStaleAsync(RightsLicenseStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RightsLicenseCase?> GetAsync(string workspaceId, Guid caseId, CancellationToken ct = default);
}

public sealed record RightsLicenseDraft(Guid CaseId, Guid ProjectId, string WorkspaceId, Guid BibliographyId, long ExpectedBibliographyRevision, string ExpectedBibliographyDigest, Guid AssetId, AssetKind AssetKind, string AssetReference, string AssetDigest, int AssetVersion, string RightsHolder, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record RightsLicenseValidateCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, LicenseScope Scope, DateTimeOffset? ValidFromUtc, DateTimeOffset? ValidUntilUtc, IReadOnlyList<string> Restrictions, string Evidence, string Actor, string RequestFingerprint);
public sealed record RightsLicenseDecisionCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, RightsLicenseDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record RightsLicenseReopenCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record RightsLicenseStaleCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record LicenseScope(string LicenseType, IReadOnlyList<string> Territories, IReadOnlyList<string> Languages, IReadOnlyList<string> Channels, bool CommercialUse, bool DerivativesAllowed, bool AttributionRequired);

public sealed record RightsLicenseCase(Guid CaseId, Guid ProjectId, string WorkspaceId, Guid BibliographyId, long ExpectedBibliographyRevision, string ExpectedBibliographyDigest, Guid AssetId, AssetKind AssetKind, string AssetReference, string AssetDigest, int AssetVersion, string RightsHolder, string Actor, string SnapshotJson, long Revision, RightsLicenseStatus Status, LicenseScope? Scope, DateTimeOffset? ValidFromUtc, DateTimeOffset? ValidUntilUtc, IReadOnlyList<string> Restrictions, string? Evidence, RightsLicenseDecision? Decision, string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record RightsLicenseCreateResult(RightsLicenseCase LicenseCase, bool Replayed);

public enum AssetKind { Text, Image, Illustration, Font, Dataset, Audio, Cover, Trademark, Other }
public enum RightsLicenseStatus { Proposed, Validated, Approved, Rejected, Expired, Revoked, Stale }
public enum RightsLicenseDecision { Approve, Reject, Revoke, MarkExpired }

public sealed class RightsLicenseValidationException : Exception { public RightsLicenseValidationException(string message) : base(message) { } }
public sealed class RightsLicenseConflictException : Exception { public RightsLicenseConflictException(string message) : base(message) { } }
public sealed class RightsLicenseTransitionException : Exception { public RightsLicenseTransitionException(string message) : base(message) { } }
