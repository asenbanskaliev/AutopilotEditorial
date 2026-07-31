using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace BookStudio.Application.Production;

public sealed class EpubRenderOrchestrator
{
    private readonly IEpubRenderStore _store;
    private readonly IEpubManuscriptAuthorityReader _authority;

    public EpubRenderOrchestrator(IEpubRenderStore store, IEpubManuscriptAuthorityReader authority)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<EpubRenderState> SubmitAsync(EpubRenderRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var snapshot = await _authority.RequireCurrentApprovedAsync(request.Manuscript, ct);
        EnsureAuthority(request, snapshot);
        return (await _store.SubmitAsync(request, at, ct)).Render;
    }

    public async ValueTask<EpubRenderState> ValidateAsync(EpubValidationCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RenderId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        return await _store.ValidateAsync(command, at, ct);
    }

    public async ValueTask<EpubRenderState> DecideAsync(EpubDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RenderId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        RequireText(command.Reason, command.Evidence, command.EvidenceDigest, command.Actor, command.RequestFingerprint);
        if (command.Decision == EpubDecision.Approve)
            EnsureApprovable(current);
        if (command.Decision == EpubDecision.Supersede && current.Status != EpubRenderStatus.Approved)
            throw new EpubRenderTransitionException("Only an approved EPUB can be superseded.");
        return await _store.DecideAsync(command, at, ct);
    }

    public static EpubPackage BuildPackage(EpubRenderRequest request, EpubManuscriptAuthoritySnapshot snapshot)
    {
        EnsureAuthority(request, snapshot);
        var entries = new List<(string Path, string MediaType, byte[] Content, EpubCompression Compression)>();
        entries.Add(("mimetype", "text/plain", Encoding.ASCII.GetBytes("application/epub+zip"), EpubCompression.Stored));
        entries.Add(("META-INF/container.xml", "application/xml", Utf8(ContainerXml()), EpubCompression.Deflated));

        var orderedSections = snapshot.Sections.OrderBy(x => x.Order).ThenBy(x => x.SectionId).ToArray();
        var xhtmlPaths = new List<string>();
        foreach (var section in orderedSections)
        {
            var path = $"OEBPS/text/{section.Order:D4}-{section.SectionId:N}.xhtml";
            xhtmlPaths.Add(path);
            entries.Add((path, "application/xhtml+xml", Utf8(RenderSection(section, request.Locale)), EpubCompression.Deflated));
        }

        foreach (var resource in request.Resources.OrderBy(x => x.Path, StringComparer.Ordinal))
            entries.Add(($"OEBPS/{resource.Path}", resource.MediaType, resource.Content, EpubCompression.Deflated));

        entries.Add(("OEBPS/nav.xhtml", "application/xhtml+xml", Utf8(RenderNavigation(request, orderedSections, xhtmlPaths)), EpubCompression.Deflated));
        entries.Add(("OEBPS/package.opf", "application/oebps-package+xml", Utf8(RenderPackageDocument(request, entries, xhtmlPaths)), EpubCompression.Deflated));

        var packageEntries = entries.Select((entry, index) => new EpubPackageEntry(
            entry.Path, entry.MediaType, Digest(entry.Content), entry.Content.LongLength, entry.Compression, index)).ToArray();
        var packageDigest = Digest(Encoding.UTF8.GetBytes(string.Join("\n", packageEntries.Select(x =>
            $"{x.Order}|{x.Path}|{x.MediaType}|{x.ContentDigest}|{x.Length}|{x.Compression}"))));
        return new EpubPackage(packageDigest, packageEntries, "OEBPS/nav.xhtml", "OEBPS/package.opf");
    }

    private async ValueTask<EpubRenderState> RequireAsync(string workspaceId, Guid renderId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, renderId, ct) ?? throw new EpubRenderValidationException("EPUB render not found.");

    private async ValueTask RequireCurrentAuthorityAsync(EpubRenderState state, CancellationToken ct)
    {
        var snapshot = await _authority.RequireCurrentApprovedAsync(state.Manuscript, ct);
        EnsureAuthority(state.Manuscript, state.WorkspaceId, state.ProjectId, snapshot);
    }

    private static void ValidateRequest(EpubRenderRequest request)
    {
        RequireText(request.WorkspaceId, request.Locale, request.Actor, request.RequestFingerprint,
            request.Metadata.Identifier, request.Metadata.Title, request.Metadata.Language,
            request.Manuscript.CanonicalContentDigest, request.Manuscript.ManifestDigest);
        if (request.ProjectId == Guid.Empty || request.RenderId == Guid.Empty || request.RequestId == Guid.Empty)
            throw new EpubRenderValidationException("Stable identities are required.");
        if (request.Metadata.Authors.Count == 0 || request.Metadata.Authors.Any(string.IsNullOrWhiteSpace))
            throw new EpubRenderValidationException("At least one governed author is required.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in request.Resources)
        {
            if (!resource.RightsApproved || resource.Content.Length == 0 || !paths.Add(resource.Path) || !SafePath(resource.Path))
                throw new EpubRenderValidationException("EPUB resources must be rights-approved, unique, non-empty and path-safe.");
            if (!StringComparer.Ordinal.Equals(Digest(resource.Content), resource.ContentDigest))
                throw new EpubRenderValidationException("EPUB resource digest mismatch.");
        }
    }

    private static void EnsureAuthority(EpubRenderRequest request, EpubManuscriptAuthoritySnapshot snapshot) =>
        EnsureAuthority(request.Manuscript, request.WorkspaceId, request.ProjectId, snapshot);

    private static void EnsureAuthority(EpubManuscriptAuthority expected, string workspaceId, Guid projectId,
        EpubManuscriptAuthoritySnapshot snapshot)
    {
        if (!snapshot.IsCurrent || !snapshot.IsApproved || snapshot.Authority != expected ||
            !StringComparer.Ordinal.Equals(expected.WorkspaceId, workspaceId) || expected.ProjectId != projectId)
            throw new EpubRenderValidationException("VS-110 manuscript authority is stale, unapproved or mismatched.");
        var sectionOrders = snapshot.Sections.Select(x => x.Order).ToArray();
        if (sectionOrders.Distinct().Count() != sectionOrders.Length || sectionOrders.Any(x => x < 0))
            throw new EpubRenderValidationException("Manuscript section ordering is invalid.");
        foreach (var node in snapshot.Sections.SelectMany(x => x.Nodes))
            if (node.Kind == EpubNodeKind.Figure && string.IsNullOrWhiteSpace(node.AccessibilityAlternative))
                throw new EpubRenderValidationException("Figures require accessibility alternatives.");
    }

    private static void EnsureApprovable(EpubRenderState state)
    {
        if (state.Package is null || state.Status is not (EpubRenderStatus.Validated or EpubRenderStatus.ReviewRequired))
            throw new EpubRenderTransitionException("EPUB must be rendered and validated before approval.");
        if (state.Findings.Any(x => x.Severity == EpubSeverity.Blocking))
            throw new EpubRenderTransitionException("Blocking EPUB findings prevent approval.");
    }

    private static void RequireRevision(EpubRenderState state, long expected)
    {
        if (state.Revision != expected) throw new EpubRenderConflictException("Stale EPUB render revision.");
    }

    private static string RenderSection(EpubSection section, string locale)
    {
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var body = new XElement(xhtml + "body", new XAttribute("data-section-kind", section.Kind.ToString()),
            new XElement(xhtml + "h1", section.Title),
            section.Nodes.OrderBy(x => x.Order).ThenBy(x => x.NodeId).Select(RenderNode));
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(xhtml + "html", new XAttribute(XNamespace.Xml + "lang", locale),
                new XElement(xhtml + "head", new XElement(xhtml + "title", section.Title)), body)).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement RenderNode(EpubNode node)
    {
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var element = node.Kind switch
        {
            EpubNodeKind.Chapter => new XElement(xhtml + "section"),
            EpubNodeKind.Scene => new XElement(xhtml + "section"),
            EpubNodeKind.Table => new XElement(xhtml + "div", new XAttribute("role", "table")),
            EpubNodeKind.Figure => new XElement(xhtml + "figure"),
            EpubNodeKind.Footnote or EpubNodeKind.Endnote => new XElement(xhtml + "aside", new XAttribute("epub:type", "footnote")),
            _ => new XElement(xhtml + "p")
        };
        element.SetAttributeValue("id", $"n-{node.NodeId:N}");
        element.Add(new XText(node.Content));
        if (!string.IsNullOrWhiteSpace(node.Caption)) element.Add(new XElement(xhtml + "figcaption", node.Caption));
        if (!string.IsNullOrWhiteSpace(node.AccessibilityAlternative)) element.SetAttributeValue("aria-label", node.AccessibilityAlternative);
        return element;
    }

    private static string RenderNavigation(EpubRenderRequest request, IReadOnlyList<EpubSection> sections, IReadOnlyList<string> paths)
    {
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        XNamespace epub = "http://www.idpf.org/2007/ops";
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(xhtml + "html", new XAttribute(XNamespace.Xml + "lang", request.Locale),
                new XAttribute(XNamespace.Xmlns + "epub", epub), new XElement(xhtml + "head", new XElement(xhtml + "title", "Contents")),
                new XElement(xhtml + "body", new XElement(xhtml + "nav", new XAttribute(epub + "type", "toc"),
                    new XElement(xhtml + "ol", sections.Select((s, i) => new XElement(xhtml + "li",
                        new XElement(xhtml + "a", new XAttribute("href", paths[i]["OEBPS/".Length..]), s.Title)))))))).ToString(SaveOptions.DisableFormatting);
    }

    private static string RenderPackageDocument(EpubRenderRequest request,
        IReadOnlyList<(string Path, string MediaType, byte[] Content, EpubCompression Compression)> entries,
        IReadOnlyList<string> spinePaths)
    {
        var manifest = entries.Where(x => x.Path.StartsWith("OEBPS/", StringComparison.Ordinal) && x.Path != "OEBPS/package.opf")
            .Select((x, i) => $"<item id=\"i{i}\" href=\"{x.Path[6..]}\" media-type=\"{x.MediaType}\"{(x.Path == "OEBPS/nav.xhtml" ? " properties=\"nav\"" : "")}/>");
        var spine = spinePaths.Select((_, i) => $"<itemref idref=\"i{i}\"/>");
        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"pub-id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"pub-id\">{Escape(request.Metadata.Identifier)}</dc:identifier><dc:title>{Escape(request.Metadata.Title)}</dc:title><dc:language>{Escape(request.Metadata.Language)}</dc:language>{string.Concat(request.Metadata.Authors.Select(a => $"<dc:creator>{Escape(a)}</dc:creator>"))}<meta property=\"dcterms:modified\">{request.Metadata.ModifiedAtUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</meta></metadata><manifest>{string.Concat(manifest)}</manifest><spine>{string.Concat(spine)}</spine></package>";
    }

    private static string ContainerXml() => "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/package.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>";
    private static bool SafePath(string path) => !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\');
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) throw new EpubRenderValidationException("Required EPUB text is missing.");
    }
}
