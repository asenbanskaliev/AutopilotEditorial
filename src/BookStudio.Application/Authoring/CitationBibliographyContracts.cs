namespace BookStudio.Application.Authoring;

public interface ICitationBibliographyStore
{
    ValueTask<CitationBibliographyCreateResult> CreateAsync(CitationBibliographyDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CitationBibliography> ValidateAsync(CitationBibliographyValidateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CitationBibliography> DecideAsync(CitationBibliographyDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CitationBibliography> ReopenAsync(CitationBibliographyReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CitationBibliography> MarkStaleAsync(CitationBibliographyStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CitationBibliography?> GetAsync(string workspaceId, Guid bibliographyId, CancellationToken ct = default);
}

public sealed record CitationBibliographyDraft(Guid BibliographyId, Guid ProjectId, string WorkspaceId, Guid ClaimVerificationId, long ExpectedClaimVerificationRevision, string ExpectedClaimVerificationDigest, int Version, string CitationStyle, string Locale, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record CitationBibliographyValidateCommand(Guid RequestId, string WorkspaceId, Guid BibliographyId, long ExpectedRevision, IReadOnlyList<CitationDraft> Citations, IReadOnlyList<BibliographyEntryDraft> Entries, string Evidence, string Actor, string RequestFingerprint);
public sealed record CitationBibliographyDecisionCommand(Guid RequestId, string WorkspaceId, Guid BibliographyId, long ExpectedRevision, CitationBibliographyDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record CitationBibliographyReopenCommand(Guid RequestId, string WorkspaceId, Guid BibliographyId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record CitationBibliographyStaleCommand(Guid RequestId, string WorkspaceId, Guid BibliographyId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record CitationDraft(Guid CitationId, Guid ClaimId, Guid SourceId, CitationKind Kind, string Location, string Locator, string RenderedText, bool MetadataValid, bool LinkValid, bool IsCurrent, string Evidence);
public sealed record BibliographyEntryDraft(Guid EntryId, Guid SourceId, string CanonicalKey, string Title, string? Author, string? Publisher, int? Year, string? Doi, string? Isbn, string? Url, string RenderedText, bool MetadataValid, bool IsCurrent, string Evidence);
public sealed record Citation(Guid CitationId, Guid ClaimId, Guid SourceId, CitationKind Kind, string Location, string Locator, string RenderedText, bool MetadataValid, bool LinkValid, bool IsCurrent, string Evidence);
public sealed record BibliographyEntry(Guid EntryId, Guid SourceId, string CanonicalKey, string Title, string? Author, string? Publisher, int? Year, string? Doi, string? Isbn, string? Url, string RenderedText, bool MetadataValid, bool IsCurrent, string Evidence);

public sealed record CitationBibliography(Guid BibliographyId, Guid ProjectId, string WorkspaceId, Guid ClaimVerificationId, long ExpectedClaimVerificationRevision, string ExpectedClaimVerificationDigest, int Version, string CitationStyle, string Locale, string Actor, string SnapshotJson, long Revision, CitationBibliographyStatus Status, IReadOnlyList<Citation> Citations, IReadOnlyList<BibliographyEntry> Entries, CitationBibliographyDecision? Decision, string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record CitationBibliographyCreateResult(CitationBibliography Bibliography, bool Replayed);

public enum CitationBibliographyStatus { Proposed, Validated, Approved, Rejected, RepairRequired, Stale }
public enum CitationBibliographyDecision { Approve, Reject, ReturnToRepair }
public enum CitationKind { Inline, Footnote, Endnote, Parenthetical, Narrative, Figure, Table, Epigraph }

public sealed class CitationBibliographyValidationException : Exception { public CitationBibliographyValidationException(string message) : base(message) { } }
public sealed class CitationBibliographyConflictException : Exception { public CitationBibliographyConflictException(string message) : base(message) { } }
public sealed class CitationBibliographyTransitionException : Exception { public CitationBibliographyTransitionException(string message) : base(message) { } }
