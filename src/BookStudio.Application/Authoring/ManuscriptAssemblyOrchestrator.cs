using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed class ManuscriptAssemblyOrchestrator
{
    private readonly IManuscriptAssemblyStore _store;
    private readonly IManuscriptAssemblyAuthorityReader _authority;

    public ManuscriptAssemblyOrchestrator(IManuscriptAssemblyStore store, IManuscriptAssemblyAuthorityReader authority)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<ManuscriptAssemblyState> SubmitAsync(ManuscriptAssemblyDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        var snapshot = await _authority.RequireCurrentAsync(draft.Authority, ct);
        if (!snapshot.IsCurrent)
            throw new ManuscriptAssemblyValidationException("Manuscript authority is stale, incomplete or digest-mismatched.");
        return (await _store.SubmitAsync(draft, at, ct)).Assembly;
    }

    public async ValueTask<ManuscriptAssemblyState> ValidateAsync(ManuscriptAssemblyValidationCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.AssemblyId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        return await _store.ValidateAsync(command, at, ct);
    }

    public async ValueTask<ManuscriptAssemblyState> DecideAsync(ManuscriptAssemblyDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.AssemblyId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        RequireText(command.Reason, command.Evidence, command.EvidenceDigest, command.Actor, command.RequestFingerprint);

        switch (command.Decision)
        {
            case ManuscriptAssemblyDecision.Approve:
                EnsureApprovable(current);
                break;
            case ManuscriptAssemblyDecision.ReturnToRepair when current.Status is not
                (ManuscriptAssemblyStatus.Validated or ManuscriptAssemblyStatus.ReviewRequired or ManuscriptAssemblyStatus.Approved):
                throw new ManuscriptAssemblyTransitionException("Assembly cannot be returned to repair from its current state.");
            case ManuscriptAssemblyDecision.Reject when current.Status is
                (ManuscriptAssemblyStatus.Approved or ManuscriptAssemblyStatus.Superseded):
                throw new ManuscriptAssemblyTransitionException("Approved or superseded assembly cannot be rejected.");
            case ManuscriptAssemblyDecision.Supersede when current.Status != ManuscriptAssemblyStatus.Approved:
                throw new ManuscriptAssemblyTransitionException("Only an approved canonical assembly can be superseded.");
        }

        return await _store.DecideAsync(command, at, ct);
    }

    public static ManuscriptCanonicalManifest BuildManifest(ManuscriptAssemblyDraft draft)
    {
        var sections = draft.Sections.OrderBy(x => x.Order).ThenBy(x => x.SectionId).ToArray();
        var nodes = sections.SelectMany(x => x.Nodes.OrderBy(n => n.Order).ThenBy(n => n.NodeId)).ToArray();
        var included = AllSources(draft.Authority).OrderBy(x => x.SliceId, StringComparer.Ordinal)
            .ThenBy(x => x.SourceId).ThenBy(x => x.Revision).ToArray();
        var excluded = draft.ExcludedOptionalSources.OrderBy(x => x.SliceId, StringComparer.Ordinal)
            .ThenBy(x => x.SourceId).ThenBy(x => x.Revision).ToArray();

        var canonical = string.Join("\n", sections.SelectMany(section =>
            new[] { $"SECTION:{section.Kind}:{section.Order}:{section.SectionId:D}:{section.Title}" }
                .Concat(section.Nodes.OrderBy(n => n.Order).ThenBy(n => n.NodeId)
                    .Select(node => $"NODE:{node.Kind}:{node.Order}:{node.NodeId:D}:{node.ContentDigest}:{node.Content}"))));
        var manifest = string.Join("|", included.Select(SourceToken)) + "||" + string.Join("|", excluded.Select(SourceToken));

        return new ManuscriptCanonicalManifest(
            Digest(canonical), Digest(manifest), sections.Select(x => x.SectionId).ToArray(),
            nodes.Select(x => x.NodeId).ToArray(), included, excluded);
    }

    private async ValueTask<ManuscriptAssemblyState> RequireAsync(string workspaceId, Guid assemblyId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, assemblyId, ct)
        ?? throw new ManuscriptAssemblyValidationException("Manuscript assembly not found.");

    private async ValueTask RequireCurrentAuthorityAsync(ManuscriptAssemblyState current, CancellationToken ct)
    {
        var snapshot = await _authority.RequireCurrentAsync(current.Authority, ct);
        if (!snapshot.IsCurrent)
            throw new ManuscriptAssemblyValidationException("Manuscript authority drift detected.");
    }

    private static void ValidateDraft(ManuscriptAssemblyDraft draft)
    {
        if (draft.RequestId == Guid.Empty || draft.AssemblyId == Guid.Empty || draft.ProjectId == Guid.Empty)
            throw new ManuscriptAssemblyValidationException("Request, assembly and project identifiers are required.");
        RequireText(draft.WorkspaceId, draft.Locale, draft.Actor, draft.RequestFingerprint);
        if (draft.TargetChannels.Count == 0 || draft.TargetChannels.Distinct().Count() != draft.TargetChannels.Count)
            throw new ManuscriptAssemblyValidationException("At least one unique target channel is required.");
        if (draft.Sections.Count == 0)
            throw new ManuscriptAssemblyValidationException("At least one manuscript section is required.");

        var sectionOrders = draft.Sections.Select(x => x.Order).ToArray();
        if (sectionOrders.Any(x => x < 0) || sectionOrders.Distinct().Count() != sectionOrders.Length)
            throw new ManuscriptAssemblyValidationException("Section order must be explicit, non-negative and unique.");
        if (draft.Sections.Select(x => x.SectionId).Any(x => x == Guid.Empty) ||
            draft.Sections.Select(x => x.SectionId).Distinct().Count() != draft.Sections.Count)
            throw new ManuscriptAssemblyValidationException("Section identifiers must be unique and non-empty.");

        foreach (var section in draft.Sections)
        {
            RequireText(section.Title);
            if (section.Nodes.Count == 0)
                throw new ManuscriptAssemblyValidationException("Each section must contain at least one node.");
            var nodeOrders = section.Nodes.Select(x => x.Order).ToArray();
            if (nodeOrders.Any(x => x < 0) || nodeOrders.Distinct().Count() != nodeOrders.Length)
                throw new ManuscriptAssemblyValidationException("Node order must be explicit, non-negative and unique within a section.");
            foreach (var node in section.Nodes)
            {
                if (node.NodeId == Guid.Empty) throw new ManuscriptAssemblyValidationException("Node identifier is required.");
                RequireText(node.Content, node.ContentDigest);
                if (node.Kind == ManuscriptContentKind.Figure && string.IsNullOrWhiteSpace(node.AccessibilityAlternative))
                    throw new ManuscriptAssemblyValidationException("Figures require an approved accessibility alternative.");
                if (node.SourceIds.Count == 0)
                    throw new ManuscriptAssemblyValidationException("Every canonical node requires exact source authority.");
            }
        }

        var allNodes = draft.Sections.SelectMany(x => x.Nodes).ToArray();
        if (allNodes.Select(x => x.NodeId).Distinct().Count() != allNodes.Length)
            throw new ManuscriptAssemblyValidationException("Content node identifiers must be globally unique.");

        var sources = AllSources(draft.Authority).ToArray();
        if (sources.Length == 0) throw new ManuscriptAssemblyValidationException("Approved upstream authority is required.");
        foreach (var source in sources)
        {
            if (source.SourceId == Guid.Empty || source.ProjectId != draft.ProjectId || source.Revision < 1)
                throw new ManuscriptAssemblyValidationException("Source identity, project and revision must match the assembly.");
            RequireText(source.SliceId, source.ContentDigest, source.EvidenceDigest, source.WorkspaceId);
            if (!StringComparer.Ordinal.Equals(source.WorkspaceId, draft.WorkspaceId))
                throw new ManuscriptAssemblyValidationException("Cross-workspace source authority is forbidden.");
            if (source.Status is not (ManuscriptSourceStatus.Approved or ManuscriptSourceStatus.Pass))
                throw new ManuscriptAssemblyValidationException("Only approved or PASS source authority may be assembled.");
        }
        if (sources.Select(x => x.SourceId).Distinct().Count() != sources.Length)
            throw new ManuscriptAssemblyValidationException("Each included source must appear exactly once.");
        var known = sources.Select(x => x.SourceId).ToHashSet();
        if (allNodes.SelectMany(x => x.SourceIds).Any(id => !known.Contains(id)))
            throw new ManuscriptAssemblyValidationException("Content node references missing source authority.");

        _ = BuildManifest(draft);
    }

    private static IEnumerable<ManuscriptSourceBinding> AllSources(ManuscriptAssemblyAuthority authority) =>
        authority.EditorialSources.Concat(authority.ResearchSources).Concat(authority.RightsSources)
            .Concat(authority.VisualSources).Concat(authority.AccessibilitySources)
            .Concat(authority.CoverSource is null ? [] : new[] { authority.CoverSource });

    private static void EnsureApprovable(ManuscriptAssemblyState current)
    {
        if (current.Status != ManuscriptAssemblyStatus.Validated || current.Manifest is null)
            throw new ManuscriptAssemblyTransitionException("Only a validated canonical manuscript with a manifest can be approved.");
        if (current.Findings.Any(x => x.Severity == ManuscriptAssemblySeverity.Blocking))
            throw new ManuscriptAssemblyTransitionException("Blocking manuscript findings prevent approval.");
    }

    private static void RequireRevision(ManuscriptAssemblyState current, long expectedRevision)
    {
        if (current.Revision != expectedRevision)
            throw new ManuscriptAssemblyConflictException("Stale manuscript assembly revision.");
    }

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ManuscriptAssemblyValidationException("Required manuscript text or evidence is missing.");
    }

    private static string SourceToken(ManuscriptSourceBinding source) =>
        $"{source.SliceId}:{source.SourceId:D}:{source.Revision}:{source.ContentDigest}:{source.EvidenceDigest}:{source.Status}";

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
