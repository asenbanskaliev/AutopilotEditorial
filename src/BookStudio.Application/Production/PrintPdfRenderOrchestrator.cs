using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Production;

public sealed class PrintPdfRenderOrchestrator
{
    private readonly IPrintPdfRenderStore _store;
    private readonly IPrintPdfAuthorityReader _authority;

    public PrintPdfRenderOrchestrator(IPrintPdfRenderStore store, IPrintPdfAuthorityReader authority)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<PrintPdfRenderState> SubmitAsync(PrintPdfRenderRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var snapshot = await _authority.RequireCurrentApprovedAsync(request.Authority, ct);
        RequireAuthority(request, snapshot);
        var artifact = BuildArtifact(request, snapshot);
        return (await _store.SubmitAsync(request, artifact, at, ct)).Render;
    }

    public async ValueTask<PrintPdfRenderState> ValidateAsync(PrintPdfValidationCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RenderId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        return await _store.ValidateAsync(command, at, ct);
    }

    public async ValueTask<PrintPdfRenderState> DecideAsync(PrintPdfDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RenderId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        RequireText(command.Reason, command.Evidence, command.EvidenceDigest, command.Actor, command.RequestFingerprint);

        if (command.Decision == PrintPdfDecision.Approve)
            EnsureApprovable(current);
        if (command.Decision == PrintPdfDecision.Supersede && current.Status != PrintPdfRenderStatus.Approved)
            throw new PrintPdfTransitionException("Only an approved print PDF can be superseded.");
        if (command.Decision == PrintPdfDecision.Reject && current.Status is PrintPdfRenderStatus.Approved or PrintPdfRenderStatus.Superseded)
            throw new PrintPdfTransitionException("Approved or superseded print PDF cannot be rejected.");

        return await _store.DecideAsync(command, at, ct);
    }

    public static PrintPdfArtifact BuildArtifact(PrintPdfRenderRequest request, PrintPdfAuthoritySnapshot snapshot)
    {
        ValidateGeometry(request.Geometry);
        ValidateFonts(request.Fonts);
        ValidateImages(request.Images);

        var pages = snapshot.SourcePages
            .OrderBy(x => x.Order)
            .ThenBy(x => x.SourceId)
            .Select((source, index) =>
            {
                var pageNumber = index + 1;
                var side = pageNumber % 2 == 1 ? PrintPageSide.Recto : PrintPageSide.Verso;
                var boxes = Boxes(request.Geometry);
                var pageId = DeterministicGuid($"print-page:{request.RenderId:D}:{pageNumber}:{source.SourceId:D}");
                var digest = Digest($"{source.ContentDigest}|{pageNumber}|{source.Kind}|{side}|{BoxesToken(boxes)}");
                return new PrintPageManifestEntry(pageId, pageNumber, source.Kind, side, digest, boxes);
            })
            .ToArray();

        var fontIds = request.Fonts.OrderBy(x => x.Family, StringComparer.Ordinal)
            .ThenBy(x => x.Style, StringComparer.Ordinal).ThenBy(x => x.FontId)
            .Select(x => x.FontId).ToArray();
        var imageIds = request.Images.OrderBy(x => x.ImageId).Select(x => x.ImageId).ToArray();
        var metadataDigest = Digest($"{request.Metadata.Identifier}|{request.Metadata.Title}|{string.Join('|', request.Metadata.Authors)}|{request.Metadata.Language}|{request.Metadata.ModifiedAtUtc:O}");
        var artifactDigest = Digest(string.Join("\n", pages.Select(x => $"{x.PageNumber}:{x.PageId:D}:{x.ContentDigest}")) +
                                    "\nFONTS:" + string.Join(',', fontIds) +
                                    "\nIMAGES:" + string.Join(',', imageIds) +
                                    "\nMETA:" + metadataDigest +
                                    "\nOUTPUT:" + request.Paper.OutputIntentDigest);

        return new PrintPdfArtifact(artifactDigest, pages, fontIds, imageIds, metadataDigest, request.Paper.OutputIntentDigest);
    }

    private async ValueTask<PrintPdfRenderState> RequireAsync(string workspaceId, Guid renderId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, renderId, ct)
        ?? throw new PrintPdfValidationException("Print PDF render not found.");

    private async ValueTask RequireCurrentAuthorityAsync(PrintPdfRenderState state, CancellationToken ct)
    {
        var snapshot = await _authority.RequireCurrentApprovedAsync(state.Authority, ct);
        RequireAuthority(state.WorkspaceId, state.ProjectId, state.Authority, snapshot);
    }

    private static void RequireAuthority(PrintPdfRenderRequest request, PrintPdfAuthoritySnapshot snapshot) =>
        RequireAuthority(request.WorkspaceId, request.ProjectId, request.Authority, snapshot);

    private static void RequireAuthority(string workspaceId, Guid projectId, PrintPdfAuthority expected, PrintPdfAuthoritySnapshot snapshot)
    {
        if (!snapshot.IsCurrent || !snapshot.IsApproved || snapshot.Authority != expected ||
            !StringComparer.Ordinal.Equals(expected.WorkspaceId, workspaceId) || expected.ProjectId != projectId)
            throw new PrintPdfValidationException("Print PDF authority is stale, unapproved, cross-workspace or digest-mismatched.");
    }

    private static void ValidateRequest(PrintPdfRenderRequest request)
    {
        if (request.RequestId == Guid.Empty || request.RenderId == Guid.Empty || request.ProjectId == Guid.Empty)
            throw new PrintPdfValidationException("Stable identities are required.");
        RequireText(request.WorkspaceId, request.Locale, request.Actor, request.RequestFingerprint,
            request.Metadata.Identifier, request.Metadata.Title, request.Metadata.Language,
            request.Paper.ProfileId, request.Paper.ColorSpace, request.Paper.OutputIntentDigest,
            request.Authority.PackageDigest, request.Authority.Status);
        if (!StringComparer.OrdinalIgnoreCase.Equals(request.Authority.Status, "APPROVED"))
            throw new PrintPdfValidationException("VS-111 authority must be approved.");
        if (request.Metadata.Authors.Count == 0 || request.Metadata.Authors.Any(string.IsNullOrWhiteSpace))
            throw new PrintPdfValidationException("At least one governed author is required.");
    }

    private static void ValidateGeometry(PrintGeometry geometry)
    {
        if (geometry.TrimWidthPoints <= 0 || geometry.TrimHeightPoints <= 0 ||
            geometry.BleedTopPoints < 0 || geometry.BleedRightPoints < 0 || geometry.BleedBottomPoints < 0 || geometry.BleedLeftPoints < 0 ||
            geometry.MarginTopPoints <= 0 || geometry.MarginOutsidePoints <= 0 || geometry.MarginBottomPoints <= 0 || geometry.MarginInsidePoints <= 0)
            throw new PrintPdfValidationException("Print geometry is invalid.");
        if (geometry.MarginInsidePoints + geometry.MarginOutsidePoints >= geometry.TrimWidthPoints ||
            geometry.MarginTopPoints + geometry.MarginBottomPoints >= geometry.TrimHeightPoints)
            throw new PrintPdfValidationException("Margins consume the trim area.");
    }

    private static void ValidateFonts(IReadOnlyList<PrintFontResource> fonts)
    {
        if (fonts.Count == 0) throw new PrintPdfValidationException("At least one embedded font is required.");
        foreach (var font in fonts)
        {
            RequireText(font.Family, font.Style, font.ContentDigest);
            if (!font.EmbeddingPermitted || !font.Embedded)
                throw new PrintPdfValidationException("All fonts must permit embedding and be embedded.");
            if (font.Glyphs.Count == 0)
                throw new PrintPdfValidationException("Font glyph coverage evidence is required.");
        }
        if (fonts.Select(x => x.FontId).Distinct().Count() != fonts.Count)
            throw new PrintPdfConflictException("Duplicate font identity.");
    }

    private static void ValidateImages(IReadOnlyList<PrintImageResource> images)
    {
        foreach (var image in images)
        {
            RequireText(image.ContentDigest, image.ColorProfile);
            if (!image.RightsApproved || image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
                image.PlacedWidthPoints <= 0 || image.PlacedHeightPoints <= 0)
                throw new PrintPdfValidationException("Image rights or geometry are invalid.");
            var horizontalDpi = image.PixelWidth / (image.PlacedWidthPoints / 72m);
            var verticalDpi = image.PixelHeight / (image.PlacedHeightPoints / 72m);
            if (horizontalDpi < 300m || verticalDpi < 300m)
                throw new PrintPdfValidationException("Image effective DPI is below 300.");
        }
        if (images.Select(x => x.ImageId).Distinct().Count() != images.Count)
            throw new PrintPdfConflictException("Duplicate image identity.");
    }

    private static void EnsureApprovable(PrintPdfRenderState state)
    {
        if (state.Artifact is null || state.Status is not (PrintPdfRenderStatus.Validated or PrintPdfRenderStatus.ReviewRequired))
            throw new PrintPdfTransitionException("Print PDF must be rendered and validated before approval.");
        if (state.Findings.Any(x => x.Severity == PrintPdfSeverity.Blocking))
            throw new PrintPdfTransitionException("Blocking print findings prevent approval.");
    }

    private static void RequireRevision(PrintPdfRenderState state, long expected)
    {
        if (state.Revision != expected) throw new PrintPdfConflictException("Stale print PDF revision.");
    }

    private static PrintPageBoxes Boxes(PrintGeometry geometry) =>
        new(
            geometry.TrimWidthPoints + geometry.BleedLeftPoints + geometry.BleedRightPoints,
            geometry.TrimHeightPoints + geometry.BleedTopPoints + geometry.BleedBottomPoints,
            geometry.BleedLeftPoints,
            geometry.BleedBottomPoints,
            geometry.TrimWidthPoints,
            geometry.TrimHeightPoints,
            0,
            0,
            geometry.TrimWidthPoints + geometry.BleedLeftPoints + geometry.BleedRightPoints,
            geometry.TrimHeightPoints + geometry.BleedTopPoints + geometry.BleedBottomPoints);

    private static string BoxesToken(PrintPageBoxes boxes) =>
        $"{boxes.MediaWidth}:{boxes.MediaHeight}:{boxes.TrimX}:{boxes.TrimY}:{boxes.TrimWidth}:{boxes.TrimHeight}:{boxes.BleedX}:{boxes.BleedY}:{boxes.BleedWidth}:{boxes.BleedHeight}";

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) throw new PrintPdfValidationException("Required text is missing.");
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
