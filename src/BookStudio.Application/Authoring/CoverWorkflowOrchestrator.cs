using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed class CoverWorkflowOrchestrator
{
    private readonly ICoverWorkflowStore _store;
    private readonly ICoverAuthorityReader _authority;

    public CoverWorkflowOrchestrator(ICoverWorkflowStore store, ICoverAuthorityReader authority)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<CoverProjectState> SubmitAsync(
        CoverProjectDraft draft,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ValidateDraft(draft);
        var snapshot = await _authority.RequireCurrentAsync(draft.Authority, ct);
        if (!snapshot.IsCurrent)
            throw new CoverWorkflowValidationException("Cover authority is stale or incomplete.");
        var submitted = await _store.SubmitAsync(draft, at, ct);
        return submitted.Project;
    }

    public async ValueTask<CoverProjectState> AddVariantAsync(
        CoverVariantCommand command,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.CoverProjectId, ct);
        if (current.Revision != command.ExpectedRevision)
            throw new CoverWorkflowConflictException("Stale cover project revision.");

        var snapshot = await _authority.RequireCurrentAsync(current.Authority, ct);
        if (!snapshot.IsCurrent)
            throw new CoverWorkflowValidationException("Cover authority drift detected.");

        ValidateVariant(current, command.Variant);
        return await _store.AddVariantAsync(command, at, ct);
    }

    public async ValueTask<CoverProjectState> DecideAsync(
        CoverDecisionCommand command,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.CoverProjectId, ct);
        if (current.Revision != command.ExpectedRevision)
            throw new CoverWorkflowConflictException("Stale cover project revision.");

        var snapshot = await _authority.RequireCurrentAsync(current.Authority, ct);
        if (!snapshot.IsCurrent)
            throw new CoverWorkflowValidationException("Cover authority drift detected.");

        var variant = current.Variants.SingleOrDefault(v => v.Draft.VariantId == command.VariantId)
            ?? throw new CoverWorkflowValidationException("Cover variant not found.");
        RequireText(command.Reason, command.Evidence, command.EvidenceDigest, command.Actor, command.RequestFingerprint);

        switch (command.Decision)
        {
            case CoverDecision.Select when variant.Status != CoverVariantStatus.Validated:
                throw new CoverWorkflowTransitionException("Only validated variants can be selected.");
            case CoverDecision.Approve:
                if (variant.Status != CoverVariantStatus.Selected)
                    throw new CoverWorkflowTransitionException("Only the selected variant can be approved.");
                EnsureRequiredCoverage(current, variant);
                break;
            case CoverDecision.ReturnToRepair when variant.Status is not (CoverVariantStatus.Validated or CoverVariantStatus.Selected or CoverVariantStatus.Approved):
                throw new CoverWorkflowTransitionException("Variant cannot be returned to repair from its current state.");
            case CoverDecision.Reject when variant.Status is CoverVariantStatus.Approved or CoverVariantStatus.Superseded:
                throw new CoverWorkflowTransitionException("Approved or superseded variants cannot be rejected.");
            case CoverDecision.Supersede when variant.Status != CoverVariantStatus.Approved:
                throw new CoverWorkflowTransitionException("Only approved variants can be superseded.");
        }

        return await _store.DecideAsync(command, at, ct);
    }

    private async ValueTask<CoverProjectState> RequireAsync(string workspaceId, Guid coverProjectId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, coverProjectId, ct)
        ?? throw new CoverWorkflowValidationException("Cover project not found.");

    private static void ValidateDraft(CoverProjectDraft draft)
    {
        if (draft.RequestId == Guid.Empty || draft.CoverProjectId == Guid.Empty || draft.ProjectId == Guid.Empty)
            throw new CoverWorkflowValidationException("Request, cover project and project identifiers are required.");
        RequireText(draft.WorkspaceId, draft.Title, draft.Author, draft.Actor, draft.RequestFingerprint,
            draft.Authority.VisualBriefDigest);
        if (draft.RequiredChannels.Count == 0 || draft.Authority.VisualBriefRevision < 1 || draft.Authority.Assets.Count == 0)
            throw new CoverWorkflowValidationException("Required channels and exact upstream authority are required.");
        if (draft.Authority.Assets.Any(a => a.AssetId == Guid.Empty || a.Revision < 1 || string.IsNullOrWhiteSpace(a.ContentDigest)))
            throw new CoverWorkflowValidationException("Every cover asset requires exact revision and digest authority.");
        if (draft.Authority.VisualAudits.Count == 0 || draft.Authority.VisualAudits.Any(a =>
            a.AuditId == Guid.Empty || a.Revision < 1 || !StringComparer.OrdinalIgnoreCase.Equals(a.Outcome, "PASS") || string.IsNullOrWhiteSpace(a.EvidenceDigest)))
            throw new CoverWorkflowValidationException("Passing VS-103 visual-audit authority is required.");
    }

    private static void ValidateVariant(CoverProjectState project, CoverVariantDraft variant)
    {
        if (variant.VariantId == Guid.Empty || !project.RequiredChannels.Contains(variant.Channel))
            throw new CoverWorkflowValidationException("Variant identity and required channel are invalid.");
        RequireText(variant.ExportProfile, variant.ArtifactDigest, variant.Typography.Title.Text,
            variant.Typography.Title.FontFamily, variant.Typography.Author.Text, variant.Typography.Author.FontFamily);
        ValidateGeometry(variant);
        ValidateTypography(variant);
        ValidatePlacements(project, variant);
        ValidateEvidence(variant);
    }

    private static void ValidateGeometry(CoverVariantDraft variant)
    {
        var g = variant.Geometry;
        if (g.Width <= 0 || g.Height <= 0 || g.SafeInset < 0 || g.Bleed < 0)
            throw new CoverWorkflowValidationException("Cover geometry must be positive and bounded.");
        if (variant.Channel == CoverChannel.Print)
        {
            if (variant.Kind != CoverVariantKind.FullWrap || g.Spine is null || g.Back is null || g.BarcodeZone is null
                || g.SpineWidth <= 0 || g.Bleed <= 0)
                throw new CoverWorkflowValidationException("Print covers require full-wrap spine, back, barcode and bleed geometry.");
        }
        else if (variant.Kind == CoverVariantKind.FullWrap && variant.Channel != CoverChannel.Print)
            throw new CoverWorkflowValidationException("Full-wrap geometry is reserved for print covers.");
    }

    private static void ValidateTypography(CoverVariantDraft variant)
    {
        var blocks = new[] { variant.Typography.Title, variant.Typography.Subtitle, variant.Typography.Author,
            variant.Typography.Series, variant.Typography.Imprint, variant.Typography.Blurb }.Where(x => x is not null).Cast<CoverTextBlock>();
        foreach (var block in blocks)
        {
            if (block.FontSize < block.MinimumFontSize || block.MinimumFontSize <= 0 || block.ContrastRatio < 3m
                || block.Bounds.Width <= 0 || block.Bounds.Height <= 0 || block.HierarchyLevel < 1)
                throw new CoverWorkflowValidationException("Typography violates hierarchy, contrast or legibility requirements.");
        }
        if (variant.Typography.Title.HierarchyLevel >= variant.Typography.Author.HierarchyLevel)
            throw new CoverWorkflowValidationException("Title must have stronger hierarchy than author text.");
    }

    private static void ValidatePlacements(CoverProjectState project, CoverVariantDraft variant)
    {
        if (variant.Placements.Count == 0)
            throw new CoverWorkflowValidationException("At least one authoritative asset placement is required.");
        foreach (var placement in variant.Placements)
        {
            var authority = project.Authority.Assets.SingleOrDefault(a => a.AssetId == placement.AssetId)
                ?? throw new CoverWorkflowValidationException("Placement references an unauthorized asset.");
            if (authority.Revision != placement.AssetRevision || !StringComparer.Ordinal.Equals(authority.ContentDigest, placement.AssetDigest)
                || placement.Bounds.Width <= 0 || placement.Bounds.Height <= 0 || string.IsNullOrWhiteSpace(placement.LineageEvidenceDigest))
                throw new CoverWorkflowValidationException("Placement authority, geometry or lineage is invalid.");
        }
    }

    private static void ValidateEvidence(CoverVariantDraft variant)
    {
        var required = new HashSet<CoverValidationKind>
        {
            CoverValidationKind.Geometry, CoverValidationKind.SafeZone, CoverValidationKind.Typography,
            CoverValidationKind.Contrast, CoverValidationKind.Crop, CoverValidationKind.Lineage,
            CoverValidationKind.ChannelFitness
        };
        if (variant.Channel == CoverChannel.Print)
        {
            required.Add(CoverValidationKind.Bleed);
            required.Add(CoverValidationKind.Spine);
            required.Add(CoverValidationKind.Barcode);
        }
        if (variant.Channel == CoverChannel.Thumbnail)
            required.Add(CoverValidationKind.ThumbnailLegibility);

        var grouped = variant.Validations.GroupBy(v => v.Kind).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var kind in required)
        {
            if (!grouped.TryGetValue(kind, out var evidence) || evidence.Length != 1 || evidence[0].Outcome != CoverValidationOutcome.Pass
                || string.IsNullOrWhiteSpace(evidence[0].Evidence) || string.IsNullOrWhiteSpace(evidence[0].EvidenceDigest))
                throw new CoverWorkflowValidationException($"Complete passing validation evidence is required for {kind}.");
        }
    }

    private static void EnsureRequiredCoverage(CoverProjectState project, CoverVariant variant)
    {
        if (!project.RequiredChannels.Contains(variant.Draft.Channel))
            throw new CoverWorkflowValidationException("Selected variant does not satisfy a required channel.");
        if (variant.Draft.Channel is CoverChannel.Thumbnail && variant.Draft.SourceVariantId is null)
            throw new CoverWorkflowValidationException("Thumbnail variants require explicit approved source lineage.");
    }

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new CoverWorkflowValidationException("Required cover workflow evidence is missing.");
    }

    internal static Guid DeterministicMessageId(Guid id, long revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"cover-workflow:{id:D}:{revision}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
