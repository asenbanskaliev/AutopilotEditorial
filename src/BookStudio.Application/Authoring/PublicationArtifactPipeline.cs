using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed record PublicationArtifactRequest(
    Guid ProofId,
    string WorkspaceId,
    string Title,
    string Author,
    string Language,
    string Manuscript,
    IReadOnlySet<string> RequiredFormats,
    decimal MaximumCost,
    string CostCurrency);

public sealed record PublicationArtifactQuote(string ProviderId, decimal Cost, string Currency);

public sealed record PublicationArtifactResult(
    IReadOnlyList<DeepBookArtifact> Artifacts,
    decimal Cost,
    string Currency,
    string ProviderId,
    bool ReusedExistingArtifacts);

public interface IPublicationArtifactProvider
{
    string ProviderId { get; }
    IReadOnlySet<string> SupportedFormats { get; }
    PublicationArtifactQuote Quote(PublicationArtifactRequest request);
    ValueTask<PublicationArtifactResult> ProduceAsync(PublicationArtifactRequest request, string workspaceRoot, CancellationToken ct = default);
}

public sealed class PublicationArtifactPipeline
{
    private readonly IReadOnlyDictionary<string, IPublicationArtifactProvider> _providers;

    public PublicationArtifactPipeline(IEnumerable<IPublicationArtifactProvider> providers)
    {
        _providers = providers.ToDictionary(x => x.ProviderId, StringComparer.OrdinalIgnoreCase);
        if (_providers.Count == 0) throw new DeepBookProofValidationException("At least one publication artifact provider is required.");
    }

    public async ValueTask<PublicationArtifactResult> ProduceAsync(
        PublicationArtifactRequest request,
        string providerId,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        Validate(request, workspaceRoot);
        if (!_providers.TryGetValue(providerId, out var provider))
            throw new DeepBookProofValidationException($"Unknown publication provider '{providerId}'.");

        var missing = request.RequiredFormats.Where(x => !provider.SupportedFormats.Contains(x)).ToArray();
        if (missing.Length > 0)
            throw new DeepBookProofValidationException($"Provider '{providerId}' does not support: {string.Join(", ", missing)}.");

        var quote = provider.Quote(request);
        if (!string.Equals(quote.Currency, request.CostCurrency, StringComparison.OrdinalIgnoreCase))
            throw new DeepBookProofValidationException("Provider quote currency does not match the proof policy.");
        if (quote.Cost < 0 || quote.Cost > request.MaximumCost)
            throw new DeepBookProofValidationException("Provider quote exceeds the configured cost ceiling.");

        var result = await provider.ProduceAsync(request, workspaceRoot, ct);
        if (result.Cost > request.MaximumCost || result.Cost < 0)
            throw new DeepBookProofValidationException("Provider result exceeds the configured cost ceiling.");
        if (!string.Equals(result.Currency, request.CostCurrency, StringComparison.OrdinalIgnoreCase))
            throw new DeepBookProofValidationException("Provider result currency does not match the proof policy.");

        var formats = result.Artifacts.Select(x => x.Format).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!request.RequiredFormats.All(formats.Contains))
            throw new DeepBookProofValidationException("Provider did not produce every required publication format.");
        if (result.Artifacts.Any(x => !x.Verified || x.ByteSize <= 0 || string.IsNullOrWhiteSpace(x.Sha256) || string.IsNullOrWhiteSpace(x.Provenance)))
            throw new DeepBookProofValidationException("Provider returned unverified or incomplete artifact evidence.");

        return result;
    }

    private static void Validate(PublicationArtifactRequest request, string workspaceRoot)
    {
        if (request.ProofId == Guid.Empty || string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new DeepBookProofValidationException("Proof and workspace identities are required.");
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Author) || string.IsNullOrWhiteSpace(request.Manuscript))
            throw new DeepBookProofValidationException("Title, author and manuscript are required.");
        if (request.RequiredFormats.Count == 0 || request.MaximumCost < 0 || string.IsNullOrWhiteSpace(request.CostCurrency))
            throw new DeepBookProofValidationException("Required formats and a valid cost policy are required.");
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new DeepBookProofValidationException("Workspace root is required.");
    }
}

public sealed class LocalDeterministicPublicationProvider : IPublicationArtifactProvider
{
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase) { "EPUB", "PDF", "DOCX", "KDP" };
    public string ProviderId => "local-deterministic-v1";
    public IReadOnlySet<string> SupportedFormats => Formats;

    public PublicationArtifactQuote Quote(PublicationArtifactRequest request) => new(ProviderId, 0m, request.CostCurrency);

    public async ValueTask<PublicationArtifactResult> ProduceAsync(PublicationArtifactRequest request, string workspaceRoot, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var output = Path.GetFullPath(Path.Combine(root, "publication", request.ProofId.ToString("N")));
        EnsureInside(root, output);
        Directory.CreateDirectory(output);

        var artifacts = new List<DeepBookArtifact>();
        foreach (var format in request.RequiredFormats.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var extension = format.ToUpperInvariant() switch { "EPUB" => ".epub", "PDF" => ".pdf", "DOCX" => ".docx", "KDP" => ".zip", _ => throw new DeepBookProofValidationException($"Unsupported format '{format}'.") };
            var fileName = Slug(request.Title) + extension;
            var path = Path.GetFullPath(Path.Combine(output, fileName));
            EnsureInside(output, path);
            var bytes = Build(format, request);
            var digest = Hex(bytes);
            var reused = File.Exists(path) && Hex(await File.ReadAllBytesAsync(path, ct)) == digest;
            if (!reused) await WriteAtomicAsync(path, bytes, ct);
            var finalBytes = await File.ReadAllBytesAsync(path, ct);
            if (Hex(finalBytes) != digest) throw new DeepBookProofValidationException("Artifact bytes changed after atomic publication.");
            artifacts.Add(new DeepBookArtifact(format.ToUpperInvariant(), Path.GetRelativePath(root, path).Replace('\\', '/'), MediaType(format), finalBytes.LongLength, digest, $"provider:{ProviderId};proof:{request.ProofId:N}", true));
        }

        var allReused = artifacts.All(x => File.Exists(Path.Combine(root, x.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        return new PublicationArtifactResult(artifacts, 0m, request.CostCurrency, ProviderId, allReused);
    }

    private static byte[] Build(string format, PublicationArtifactRequest request) => format.ToUpperInvariant() switch
    {
        "EPUB" => BuildEpub(request),
        "PDF" => BuildPdf(request),
        "DOCX" => BuildDocx(request),
        "KDP" => BuildKdp(request),
        _ => throw new DeepBookProofValidationException($"Unsupported format '{format}'.")
    };

    private static byte[] BuildEpub(PublicationArtifactRequest r) => Zip([
        ("mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression),
        ("META-INF/container.xml", Utf8("<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"), CompressionLevel.Optimal),
        ("OEBPS/content.opf", Utf8($"<?xml version=\"1.0\"?><package version=\"3.0\" xmlns=\"http://www.idpf.org/2007/opf\" unique-identifier=\"bookid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"bookid\">urn:uuid:{r.ProofId}</dc:identifier><dc:title>{Xml(r.Title)}</dc:title><dc:creator>{Xml(r.Author)}</dc:creator><dc:language>{Xml(r.Language)}</dc:language></metadata><manifest><item id=\"book\" href=\"book.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"book\"/></spine></package>"), CompressionLevel.Optimal),
        ("OEBPS/book.xhtml", Utf8($"<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"{Xml(r.Language)}\"><head><title>{Xml(r.Title)}</title></head><body><h1>{Xml(r.Title)}</h1><p>{Xml(r.Manuscript).Replace("\n", "</p><p>")}</p></body></html>"), CompressionLevel.Optimal)
    ]);

    private static byte[] BuildDocx(PublicationArtifactRequest r) => Zip([
        ("[Content_Types].xml", Utf8("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"), CompressionLevel.Optimal),
        ("_rels/.rels", Utf8("<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"), CompressionLevel.Optimal),
        ("word/document.xml", Utf8($"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{Xml(r.Title)}</w:t></w:r></w:p><w:p><w:r><w:t xml:space=\"preserve\">{Xml(r.Manuscript)}</w:t></w:r></w:p><w:sectPr/></w:body></w:document>"), CompressionLevel.Optimal)
    ]);

    private static byte[] BuildPdf(PublicationArtifactRequest r)
    {
        var text = PdfText(r.Title + "\n" + r.Author + "\n\n" + r.Manuscript);
        var stream = $"BT /F1 12 Tf 72 760 Td ({text}) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>", $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var sb = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString())); sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n"); }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString()); sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n"); foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n"); sb.Append("trailer << /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildKdp(PublicationArtifactRequest r) => Zip([
        ("metadata.json", Utf8($"{{\"proofId\":\"{r.ProofId}\",\"title\":\"{Json(r.Title)}\",\"author\":\"{Json(r.Author)}\",\"language\":\"{Json(r.Language)}\"}}"), CompressionLevel.Optimal),
        ("manuscript.txt", Utf8(r.Manuscript), CompressionLevel.Optimal),
        ("README.txt", Utf8("KDP-ready package generated by the governed no-command publication provider. External upload is intentionally not performed."), CompressionLevel.Optimal)
    ]);

    private static byte[] Zip(IEnumerable<(string Name, byte[] Bytes, CompressionLevel Compression)> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) foreach (var item in entries) { var entry = zip.CreateEntry(item.Name, item.Compression); entry.LastWriteTime = DateTimeOffset.UnixEpoch; using var s = entry.Open(); s.Write(item.Bytes); }
        return ms.ToArray();
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void EnsureInside(string root, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new DeepBookProofValidationException("Artifact path escapes the workspace.");
    }
    private static string MediaType(string f) => f.ToUpperInvariant() switch { "EPUB" => "application/epub+zip", "PDF" => "application/pdf", "DOCX" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "KDP" => "application/zip", _ => "application/octet-stream" };
    private static string Slug(string value) { var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray(); return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries)).Trim('-') is { Length: > 0 } x ? x : "book"; }
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static string Json(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string PdfText(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", ") Tj 0 -16 Td (");
}
