using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Production;

public sealed class DocxRenderOrchestrator
{
    private readonly IDocxRenderStore _store;
    private readonly IDocxAuthorityReader _authority;

    public DocxRenderOrchestrator(IDocxRenderStore store, IDocxAuthorityReader authority)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<DocxRenderState> RenderAsync(DocxRenderRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var snapshot = await _authority.RequireCurrentApprovedAsync(request.Authority, ct);
        if (!snapshot.IsCurrent || !snapshot.IsApproved || snapshot.Authority != request.Authority)
            throw new DocxValidationException("DOCX authority is stale, mismatched or not approved.");
        var artifact = BuildArtifact(request);
        return (await _store.SubmitAsync(request, artifact, at, ct)).Render;
    }

    public async ValueTask<DocxRenderState> ValidateAsync(DocxValidationCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RenderId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        return await _store.ValidateAsync(command, at, ct);
    }

    public async ValueTask<DocxRenderState> DecideAsync(DocxDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RenderId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.EvidenceDigest))
            throw new DocxValidationException("Decision evidence is required.");
        if (command.Decision == DocxDecision.Approve && (current.Artifact is null || current.Findings.Any(x => x.Severity == DocxSeverity.Blocking)))
            throw new DocxTransitionException("DOCX cannot be approved with blocking findings or without an artifact.");
        return await _store.DecideAsync(command, at, ct);
    }

    public static DocxArtifact BuildArtifact(DocxRenderRequest request)
    {
        var blocks = request.Sections.OrderBy(x => x.Order).ThenBy(x => x.SectionId)
            .SelectMany(x => x.Blocks.OrderBy(b => b.Order).ThenBy(b => b.BlockId)).ToArray();
        var parts = new List<DocxPart>
        {
            new("[Content_Types].xml", "application/xml", Digest("content-types:v1"), 0),
            new("_rels/.rels", "application/vnd.openxmlformats-package.relationships+xml", Digest("root-rels:v1"), 1),
            new("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", Digest(string.Join("\n", blocks.Select(BlockToken))), 2),
            new("word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml", Digest(string.Join("|", blocks.Select(x => x.StyleId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))), 3),
            new("word/numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml", Digest("numbering:v1"), 4),
            new("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml", Digest($"{request.ProjectId:D}|{request.Locale}|{request.TemplateProfile}|{request.CompatibilityTarget}"), 5)
        };
        foreach (var resource in request.Resources.OrderBy(x => x.PartName, StringComparer.Ordinal).ThenBy(x => x.ResourceId))
            parts.Add(new DocxPart(resource.PartName, "application/octet-stream", resource.ContentDigest, parts.Count));

        var relationships = request.Resources.OrderBy(x => x.PartName, StringComparer.Ordinal).ThenBy(x => x.ResourceId)
            .Select((x, i) => new DocxRelationship($"rId{i + 1}", "word/document.xml", x.PartName, "resource", false)).ToArray();
        var manifest = string.Join("|", parts.OrderBy(x => x.Order).Select(x => $"{x.Order}:{x.PartName}:{x.ContentDigest}"));
        var metadata = $"{request.ProjectId:D}|{request.Locale}|{request.TemplateProfile}|{request.CompatibilityTarget}|{request.Authority.ArtifactDigest}";
        return new DocxArtifact(Digest(manifest + "||" + metadata), Digest(manifest), parts, relationships,
            request.Resources.Select(x => x.ResourceId).OrderBy(x => x).ToArray(), Digest(metadata));
    }

    private async ValueTask<DocxRenderState> RequireAsync(string workspaceId, Guid renderId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, renderId, ct) ?? throw new DocxValidationException("DOCX render not found.");

    private async ValueTask RequireCurrentAuthorityAsync(DocxRenderState current, CancellationToken ct)
    {
        var snapshot = await _authority.RequireCurrentApprovedAsync(current.Authority, ct);
        if (!snapshot.IsCurrent || !snapshot.IsApproved || snapshot.Authority != current.Authority)
            throw new DocxValidationException("DOCX authority is no longer current and approved.");
    }

    private static void ValidateRequest(DocxRenderRequest request)
    {
        if (request.RequestId == Guid.Empty || request.RenderId == Guid.Empty || request.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new DocxValidationException("DOCX identity is required.");
        if (request.Authority.WorkspaceId != request.WorkspaceId || request.Authority.ProjectId != request.ProjectId)
            throw new DocxValidationException("Cross-workspace or cross-project authority is forbidden.");
        if (request.Sections.Count == 0 || request.Sections.Select(x => x.Order).Distinct().Count() != request.Sections.Count)
            throw new DocxValidationException("DOCX sections require total unique ordering.");
        if (request.Resources.Any(x => !x.RightsApproved || x.PartName.Contains("..", StringComparison.Ordinal) || x.PartName.StartsWith("/", StringComparison.Ordinal)))
            throw new DocxValidationException("DOCX resources must be safe and rights-approved.");
        if (request.Resources.Any(x => x.PartName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(x.AccessibilityAlternative)))
            throw new DocxValidationException("Embedded figures require accessibility alternatives.");
    }

    private static void RequireRevision(DocxRenderState state, long expected)
    {
        if (state.Revision != expected) throw new DocxConflictException("Stale DOCX render revision.");
    }

    private static string BlockToken(DocxBlock block) => $"{block.Kind}:{block.Order}:{block.BlockId:D}:{block.StyleId}:{block.ContentDigest}:{block.Content}";
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
