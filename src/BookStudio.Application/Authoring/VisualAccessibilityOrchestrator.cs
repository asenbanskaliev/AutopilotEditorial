using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed class VisualAccessibilityOrchestrator
{
    private readonly IVisualAccessibilityStore _store;
    private readonly IVisualAccessibilityAuthorityReader _authority;

    public VisualAccessibilityOrchestrator(
        IVisualAccessibilityStore store,
        IVisualAccessibilityAuthorityReader authority)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<VisualAccessibilityState> SubmitAsync(
        VisualAccessibilityCaseDraft draft,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ValidateDraft(draft);
        var snapshot = await _authority.RequireCurrentAsync(draft.Authority, ct);
        if (!snapshot.IsCurrent)
            throw new VisualAccessibilityValidationException("Visual accessibility authority is stale or incomplete.");

        var submitted = await _store.SubmitAsync(draft, at, ct);
        return submitted.AccessibilityCase;
    }

    public async ValueTask<VisualAccessibilityState> RecordAssessmentAsync(
        VisualAccessibilityAssessmentCommand command,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.AccessibilityCaseId, ct);
        RequireCurrentRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        ValidateAssessments(current, command);
        return await _store.RecordAssessmentAsync(command, at, ct);
    }

    public async ValueTask<VisualAccessibilityState> DecideAsync(
        VisualAccessibilityDecisionCommand command,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.AccessibilityCaseId, ct);
        RequireCurrentRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        RequireText(command.Reason, command.Evidence, command.EvidenceDigest, command.Actor, command.RequestFingerprint);

        switch (command.Decision)
        {
            case VisualAccessibilityDecision.Approve:
                EnsureApprovable(current);
                break;
            case VisualAccessibilityDecision.ReturnToRepair when current.Status is not
                (VisualAccessibilityStatus.Assessed or VisualAccessibilityStatus.ReviewRequired or VisualAccessibilityStatus.Approved):
                throw new VisualAccessibilityTransitionException("Accessibility case cannot be returned to repair from its current state.");
            case VisualAccessibilityDecision.Reject when current.Status is
                (VisualAccessibilityStatus.Approved or VisualAccessibilityStatus.Superseded):
                throw new VisualAccessibilityTransitionException("Approved or superseded accessibility cases cannot be rejected.");
            case VisualAccessibilityDecision.Supersede when current.Status != VisualAccessibilityStatus.Approved:
                throw new VisualAccessibilityTransitionException("Only approved accessibility cases can be superseded.");
        }

        return await _store.DecideAsync(command, at, ct);
    }

    private async ValueTask<VisualAccessibilityState> RequireAsync(
        string workspaceId,
        Guid accessibilityCaseId,
        CancellationToken ct) =>
        await _store.GetAsync(workspaceId, accessibilityCaseId, ct)
        ?? throw new VisualAccessibilityValidationException("Visual accessibility case not found.");

    private async ValueTask RequireCurrentAuthorityAsync(VisualAccessibilityState current, CancellationToken ct)
    {
        var snapshot = await _authority.RequireCurrentAsync(current.Authority, ct);
        if (!snapshot.IsCurrent)
            throw new VisualAccessibilityValidationException("Visual accessibility authority drift detected.");
    }

    private static void RequireCurrentRevision(VisualAccessibilityState current, long expectedRevision)
    {
        if (current.Revision != expectedRevision)
            throw new VisualAccessibilityConflictException("Stale visual accessibility revision.");
    }

    private static void ValidateDraft(VisualAccessibilityCaseDraft draft)
    {
        if (draft.RequestId == Guid.Empty || draft.AccessibilityCaseId == Guid.Empty || draft.ProjectId == Guid.Empty)
            throw new VisualAccessibilityValidationException("Request, accessibility case and project identifiers are required.");
        RequireText(draft.WorkspaceId, draft.Locale, draft.Actor, draft.RequestFingerprint,
            draft.Authority.VisualBriefDigest);
        if (draft.Authority.VisualBriefId == Guid.Empty || draft.Authority.VisualBriefRevision < 1 || draft.Visuals.Count == 0)
            throw new VisualAccessibilityValidationException("Exact VS-101 authority and at least one visual use are required.");
        if (draft.Authority.Assets.Count == 0 || draft.Authority.Assets.Any(a =>
            a.AssetId == Guid.Empty || a.Revision < 1 || string.IsNullOrWhiteSpace(a.ContentDigest)))
            throw new VisualAccessibilityValidationException("Every visual requires exact VS-101 asset authority.");
        if (draft.Authority.VisualAudits.Count == 0 || draft.Authority.VisualAudits.Any(a =>
            a.AuditId == Guid.Empty || a.Revision < 1 ||
            !StringComparer.OrdinalIgnoreCase.Equals(a.Outcome, "PASS") || string.IsNullOrWhiteSpace(a.EvidenceDigest)))
            throw new VisualAccessibilityValidationException("Passing VS-103 visual-audit authority is required.");
        if (draft.Authority.ApprovedCover is { } cover &&
            (cover.CoverProjectId == Guid.Empty || cover.VariantId == Guid.Empty || cover.Revision < 1 ||
             string.IsNullOrWhiteSpace(cover.ArtifactDigest) || string.IsNullOrWhiteSpace(cover.EvidenceDigest)))
            throw new VisualAccessibilityValidationException("Approved VS-104 cover authority is incomplete.");

        var duplicatedOrders = draft.Visuals.GroupBy(v => v.ReadingOrder).Any(g => g.Count() > 1);
        if (duplicatedOrders || draft.Visuals.Any(v => v.VisualUseId == Guid.Empty || v.AssetId == Guid.Empty ||
            v.AssetRevision < 1 || v.ReadingOrder < 1 || string.IsNullOrWhiteSpace(v.AssetDigest)))
            throw new VisualAccessibilityValidationException("Visual use identity, authority and reading order must be complete and deterministic.");

        foreach (var visual in draft.Visuals)
        {
            var asset = draft.Authority.Assets.SingleOrDefault(a => a.AssetId == visual.AssetId)
                ?? throw new VisualAccessibilityValidationException("Visual use references an unauthorized asset.");
            if (asset.Revision != visual.AssetRevision || !StringComparer.Ordinal.Equals(asset.ContentDigest, visual.AssetDigest))
                throw new VisualAccessibilityValidationException("Visual use authority digest or revision does not match VS-101.");
            if (visual.Meaning == VisualMeaning.Decorative && visual.Kind != VisualUseKind.Decorative)
                throw new VisualAccessibilityValidationException("Decorative meaning must be declared explicitly as a decorative use.");
            if (visual.ContainsEssentialText && string.IsNullOrWhiteSpace(visual.EmbeddedTextEquivalent))
                throw new VisualAccessibilityValidationException("Essential text embedded in imagery requires a text equivalent.");
        }
    }

    private static void ValidateAssessments(
        VisualAccessibilityState current,
        VisualAccessibilityAssessmentCommand command)
    {
        if (command.RequestId == Guid.Empty || command.Assessments.Count == 0)
            throw new VisualAccessibilityValidationException("Assessment request and evidence are required.");
        RequireText(command.Actor, command.RequestFingerprint);

        var ids = current.Visuals.Select(v => v.VisualUseId).ToHashSet();
        if (command.Assessments.Any(a => a.AssessmentId == Guid.Empty || !ids.Contains(a.VisualUseId) ||
            string.IsNullOrWhiteSpace(a.Evidence) || string.IsNullOrWhiteSpace(a.EvidenceDigest)))
            throw new VisualAccessibilityValidationException("Assessment identity, visual scope and evidence are required.");

        foreach (var visual in current.Visuals)
        {
            var assessments = command.Assessments.Where(a => a.VisualUseId == visual.VisualUseId).ToArray();
            if (visual.Meaning == VisualMeaning.Decorative)
                RequirePassing(assessments, VisualAccessibilityAssessmentKind.DecorativeClassification);
            else
                RequirePassingText(assessments, VisualAccessibilityAssessmentKind.AltText, a => a.AltText);

            if (visual.Meaning == VisualMeaning.Complex)
                RequirePassingText(assessments, VisualAccessibilityAssessmentKind.LongDescription, a => a.LongDescription);
            if (visual.ContainsEssentialText)
                RequirePassing(assessments, VisualAccessibilityAssessmentKind.TextInImageEquivalent);

            RequirePassing(assessments, VisualAccessibilityAssessmentKind.ReadingOrder);
            if (!string.IsNullOrWhiteSpace(visual.Caption) || !string.IsNullOrWhiteSpace(visual.AssociatedTextReference))
                RequirePassing(assessments, VisualAccessibilityAssessmentKind.CaptionAssociation);

            foreach (var contrast in assessments.Where(a => a.Kind == VisualAccessibilityAssessmentKind.Contrast))
            {
                if (contrast.Contrast is null || contrast.Contrast.MeasuredRatio <= 0 || contrast.Contrast.RequiredRatio <= 0 ||
                    contrast.Contrast.Passed != (contrast.Contrast.MeasuredRatio >= contrast.Contrast.RequiredRatio) ||
                    string.IsNullOrWhiteSpace(contrast.Contrast.EvidenceDigest))
                    throw new VisualAccessibilityValidationException("Contrast evidence is incomplete or internally inconsistent.");
            }
        }
    }

    private static void EnsureApprovable(VisualAccessibilityState current)
    {
        if (current.Status is not (VisualAccessibilityStatus.Assessed or VisualAccessibilityStatus.ReviewRequired))
            throw new VisualAccessibilityTransitionException("Only assessed accessibility cases can be approved.");
        if (current.Findings.Any(f => f.Severity == VisualAccessibilitySeverity.Blocking) ||
            current.Assessments.Any(a => a.Outcome is VisualAccessibilityOutcome.Fail or VisualAccessibilityOutcome.ReviewRequired))
            throw new VisualAccessibilityTransitionException("Blocking findings or unresolved assessments prevent approval.");

        foreach (var visual in current.Visuals)
        {
            var assessments = current.Assessments.Where(a => a.VisualUseId == visual.VisualUseId).ToArray();
            if (visual.Meaning == VisualMeaning.Decorative)
                RequirePassing(assessments, VisualAccessibilityAssessmentKind.DecorativeClassification);
            else
                RequirePassingText(assessments, VisualAccessibilityAssessmentKind.AltText, a => a.AltText);
            if (visual.Meaning == VisualMeaning.Complex)
                RequirePassingText(assessments, VisualAccessibilityAssessmentKind.LongDescription, a => a.LongDescription);
            if (visual.ContainsEssentialText)
                RequirePassing(assessments, VisualAccessibilityAssessmentKind.TextInImageEquivalent);
            RequirePassing(assessments, VisualAccessibilityAssessmentKind.ReadingOrder);
        }
    }

    private static void RequirePassing(
        IReadOnlyList<VisualAccessibilityAssessment> assessments,
        VisualAccessibilityAssessmentKind kind)
    {
        var matches = assessments.Where(a => a.Kind == kind).ToArray();
        if (matches.Length != 1 || matches[0].Outcome != VisualAccessibilityOutcome.Pass)
            throw new VisualAccessibilityValidationException($"Exactly one passing {kind} assessment is required.");
    }

    private static void RequirePassingText(
        IReadOnlyList<VisualAccessibilityAssessment> assessments,
        VisualAccessibilityAssessmentKind kind,
        Func<VisualAccessibilityAssessment, string?> selector)
    {
        RequirePassing(assessments, kind);
        if (string.IsNullOrWhiteSpace(selector(assessments.Single(a => a.Kind == kind))))
            throw new VisualAccessibilityValidationException($"Passing {kind} evidence requires a textual alternative.");
    }

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new VisualAccessibilityValidationException("Required visual accessibility evidence is missing.");
    }

    internal static Guid DeterministicMessageId(Guid id, long revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"visual-accessibility:{id:D}:{revision}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
