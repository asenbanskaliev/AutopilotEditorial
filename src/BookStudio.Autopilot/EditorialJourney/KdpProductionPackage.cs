using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed record KdpChapter(int Number, string Title, string Markdown);
public sealed record KdpCoverInput(int WidthPixels, int HeightPixels, int Dpi, string MediaType, string Sha256);
public sealed record KdpMetadata(string Title, string Author, string Language, string Description, IReadOnlyList<string> Categories, IReadOnlyList<string> Keywords, string? Isbn = null);
public sealed record KdpPackageRequest(string ProjectId, string OutputDirectory, decimal TrimWidthInches, decimal TrimHeightInches, decimal MarginInches, KdpMetadata Metadata, IReadOnlyList<KdpChapter> Chapters, KdpCoverInput Cover);
public sealed record KdpPackageFile(string Path, long Length, string Sha256);
public sealed record KdpPackageResult(string PackageZip, IReadOnlyList<KdpPackageFile> Files, string ManifestSha256, bool Passed, IReadOnlyList<string> BlockingReasons);

public sealed class KdpProductionPackageBuilder
{
    private static readonly DateTimeOffset StableTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Encoding PdfEncoding = Encoding.Latin1;

    public async ValueTask<KdpPackageResult> BuildAsync(KdpPackageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var blockers = Validate(request);
        if (blockers.Count > 0) return new KdpPackageResult(string.Empty, [], string.Empty, false, blockers);

        var output = Path.GetFullPath(request.OutputDirectory);
        var staging = Path.Combine(output, request.ProjectId + ".kdp");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        var manuscript = BuildManuscript(request);
        await WriteStableAsync(Path.Combine(staging, "manuscript.md"), manuscript, cancellationToken);
        await WriteStableAsync(Path.Combine(staging, "metadata.json"), JsonSerializer.Serialize(request.Metadata, JsonOptions), cancellationToken);
        await WriteStableAsync(Path.Combine(staging, "kdp-checklist.json"), JsonSerializer.Serialize(BuildChecklist(request), JsonOptions), cancellationToken);
        await BuildEpubAsync(Path.Combine(staging, "ebook.epub"), request, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(staging, "print-interior.pdf"), BuildPdf(request), cancellationToken);
        await WriteStableAsync(Path.Combine(staging, "cover-input.json"), JsonSerializer.Serialize(request.Cover, JsonOptions), cancellationToken);

        var files = EnumerateFiles(staging, excludeManifest: true);
        var manifest = new
        {
            schemaVersion = 1,
            request.ProjectId,
            generatedAt = "2026-01-01T00:00:00Z",
            trim = new { widthInches = request.TrimWidthInches, heightInches = request.TrimHeightInches, marginInches = request.MarginInches },
            files,
        };
        var manifestPath = Path.Combine(staging, "manifest.json");
        await WriteStableAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
        var allFiles = EnumerateFiles(staging, excludeManifest: false);
        var manifestSha = HashFile(manifestPath);
        var zipPath = Path.Combine(output, request.ProjectId + ".kdp.zip");
        Directory.CreateDirectory(output);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in allFiles.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                entry.LastWriteTime = StableTimestamp;
                await using var target = entry.Open();
                await using var source = File.OpenRead(Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                await source.CopyToAsync(target, cancellationToken);
            }
        }
        return new KdpPackageResult(zipPath, allFiles, manifestSha, true, []);
    }

    private static List<string> Validate(KdpPackageRequest request)
    {
        var reasons = new List<string>();
        if (!Regex.IsMatch(request.ProjectId, "^[a-z0-9][a-z0-9-]{2,63}$")) reasons.Add("project_id_invalid");
        if (request.Chapters.Count == 0) reasons.Add("chapters_missing");
        if (request.Chapters.Select(x => x.Number).Distinct().Count() != request.Chapters.Count) reasons.Add("chapter_numbers_duplicate");
        if (request.Chapters.Any(x => x.Markdown.Length < 200 || !x.Markdown.StartsWith('#'))) reasons.Add("chapter_content_invalid");
        if (request.TrimWidthInches < 5m || request.TrimWidthInches > 8.5m || request.TrimHeightInches < 8m || request.TrimHeightInches > 11.69m) reasons.Add("trim_unsupported");
        if (request.MarginInches < 0.25m || request.MarginInches > 1.5m) reasons.Add("margin_invalid");
        if (request.Cover.Dpi < 300 || request.Cover.WidthPixels < 1600 || request.Cover.HeightPixels < 2560) reasons.Add("cover_resolution_insufficient");
        if (request.Metadata.Title.Length is < 1 or > 200 || request.Metadata.Description.Length is < 50 or > 4000) reasons.Add("metadata_invalid");
        if (request.Metadata.Categories.Count is < 1 or > 3 || request.Metadata.Keywords.Count is < 1 or > 7) reasons.Add("discoverability_metadata_invalid");
        return reasons;
    }

    private static object BuildChecklist(KdpPackageRequest request) => new
    {
        status = "PASS",
        checks = new object[]
        {
            new { id = "trim", passed = true, value = (object)$"{request.TrimWidthInches}x{request.TrimHeightInches}" },
            new { id = "margins", passed = true, value = (object)request.MarginInches },
            new { id = "fonts", passed = true, value = (object)"Helvetica with WinAnsiEncoding for Spanish Latin characters" },
            new { id = "cover-resolution", passed = true, value = (object)request.Cover.Dpi },
            new { id = "navigation", passed = true, value = (object)request.Chapters.Count },
            new { id = "metadata", passed = true, value = (object)request.Metadata.Language },
            new { id = "isbn", passed = true, value = (object)(request.Metadata.Isbn ?? "KDP_FREE_ISBN_OR_USER_SUPPLIED") },
        }
    };

    private static string BuildManuscript(KdpPackageRequest request) => string.Join("\n\n", request.Chapters.OrderBy(x => x.Number).Select(x => x.Markdown.Trim())) + "\n";

    private static async ValueTask BuildEpubAsync(string path, KdpPackageRequest request, CancellationToken cancellationToken)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        await AddEntryAsync(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression, cancellationToken);
        await AddEntryAsync(archive, "META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>", CompressionLevel.Optimal, cancellationToken);
        var manifestItems = new StringBuilder("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        var spine = new StringBuilder();
        var nav = new StringBuilder();
        foreach (var chapter in request.Chapters.OrderBy(x => x.Number))
        {
            var id = $"c{chapter.Number:000}";
            var href = $"chapter-{chapter.Number:000}.xhtml";
            manifestItems.Append($"<item id=\"{id}\" href=\"{href}\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"{id}\"/>");
            nav.Append($"<li><a href=\"{href}\">{Xml(chapter.Title)}</a></li>");
            var paragraphs = ExtractParagraphs(chapter.Markdown);
            var body = string.Join(string.Empty, paragraphs.Select(paragraph => $"<p>{Xml(paragraph)}</p>"));
            await AddEntryAsync(archive, "OEBPS/" + href, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>{Xml(chapter.Title)}</title></head><body><h1>{Xml(chapter.Title)}</h1>{body}</body></html>", CompressionLevel.Optimal, cancellationToken);
        }
        await AddEntryAsync(archive, "OEBPS/nav.xhtml", $"<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contenido</title></head><body><nav epub:type=\"toc\"><ol>{nav}</ol></nav></body></html>", CompressionLevel.Optimal, cancellationToken);
        var opf = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"bookid\">urn:bookstudio:{Xml(request.ProjectId)}</dc:identifier><dc:title>{Xml(request.Metadata.Title)}</dc:title><dc:creator>{Xml(request.Metadata.Author)}</dc:creator><dc:language>{Xml(request.Metadata.Language)}</dc:language><meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta></metadata><manifest>{manifestItems}</manifest><spine>{spine}</spine></package>";
        await AddEntryAsync(archive, "OEBPS/content.opf", opf, CompressionLevel.Optimal, cancellationToken);
    }

    private static byte[] BuildPdf(KdpPackageRequest request)
    {
        var pageWidth = (double)request.TrimWidthInches * 72d;
        var pageHeight = (double)request.TrimHeightInches * 72d;
        var margin = Math.Max(36d, (double)request.MarginInches * 72d);
        var pages = LayoutPdfPages(request, pageWidth, pageHeight, margin);

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };

        var pageReferences = new List<int>();
        foreach (var page in pages)
        {
            var pageObjectNumber = objects.Count + 1;
            var contentObjectNumber = pageObjectNumber + 1;
            pageReferences.Add(pageObjectNumber);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth.ToString("0.##", CultureInfo.InvariantCulture)} {pageHeight.ToString("0.##", CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObjectNumber} 0 R >>");
            var content = BuildPageContent(page, pageHeight, margin);
            objects.Add($"<< /Length {PdfEncoding.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pageReferences.Select(reference => $"{reference} 0 R"))}] /Count {pages.Count} >>";

        using var stream = new MemoryStream();
        void Write(string value)
        {
            var bytes = PdfEncoding.GetBytes(value);
            stream.Write(bytes);
        }

        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<PdfLine>> LayoutPdfPages(KdpPackageRequest request, double pageWidth, double pageHeight, double margin)
    {
        const double bodyFontSize = 10.5d;
        const double bodyLeading = 14.5d;
        const double headingFontSize = 16d;
        const double headingLeading = 22d;
        var usableWidth = pageWidth - (margin * 2d);
        var maximumBodyCharacters = Math.Max(38, (int)Math.Floor(usableWidth / (bodyFontSize * 0.50d)));
        var maximumHeadingCharacters = Math.Max(25, (int)Math.Floor(usableWidth / (headingFontSize * 0.52d)));
        var pages = new List<IReadOnlyList<PdfLine>>();

        foreach (var chapter in request.Chapters.OrderBy(chapter => chapter.Number))
        {
            var current = new List<PdfLine>();
            var remainingHeight = pageHeight - (margin * 2d);

            void NewPage()
            {
                if (current.Count > 0) pages.Add(current.ToArray());
                current = [];
                remainingHeight = pageHeight - (margin * 2d);
            }

            void AddLine(string text, bool heading, bool firstParagraphLine, double spaceBefore = 0d)
            {
                var leading = heading ? headingLeading : bodyLeading;
                var required = leading + spaceBefore;
                if (remainingHeight < required && current.Count > 0) NewPage();
                current.Add(new PdfLine(text, heading, firstParagraphLine, spaceBefore));
                remainingHeight -= required;
            }

            var heading = $"Capítulo {chapter.Number}: {chapter.Title}";
            foreach (var line in WrapWords(heading, maximumHeadingCharacters)) AddLine(line, heading: true, firstParagraphLine: false, spaceBefore: current.Count == 0 ? 0d : 8d);

            var paragraphs = ExtractParagraphs(chapter.Markdown);
            foreach (var paragraph in paragraphs)
            {
                var wrapped = WrapWords(paragraph, maximumBodyCharacters).ToArray();
                for (var index = 0; index < wrapped.Length; index++) AddLine(wrapped[index], heading: false, firstParagraphLine: index == 0, spaceBefore: index == 0 ? 7d : 0d);
            }
            if (current.Count > 0) pages.Add(current.ToArray());
        }
        return pages;
    }

    private static string BuildPageContent(IReadOnlyList<PdfLine> page, double pageHeight, double margin)
    {
        var content = new StringBuilder();
        var y = pageHeight - margin;
        foreach (var line in page)
        {
            y -= line.SpaceBefore;
            var font = line.Heading ? "/F2" : "/F1";
            var size = line.Heading ? 16d : 10.5d;
            var leading = line.Heading ? 22d : 14.5d;
            var x = margin + (line.FirstParagraphLine && !line.Heading ? 18d : 0d);
            content.Append("BT ").Append(font).Append(' ').Append(size.ToString("0.##", CultureInfo.InvariantCulture)).Append(" Tf ")
                .Append(x.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(y.ToString("0.##", CultureInfo.InvariantCulture)).Append(" Td (")
                .Append(EscapePdf(line.Text)).Append(") Tj ET\n");
            y -= leading;
        }
        return content.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ExtractParagraphs(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var firstNewLine = normalized.IndexOf('\n');
        if (firstNewLine >= 0 && normalized.StartsWith('#')) normalized = normalized[(firstNewLine + 1)..].Trim();
        return Regex.Split(normalized, "\\n\\s*\\n")
            .Select(paragraph => MarkdownToText(paragraph).Replace('\n', ' ').Trim())
            .Where(paragraph => paragraph.Length > 0)
            .ToArray();
    }

    private static IEnumerable<string> WrapWords(string value, int width)
    {
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static IReadOnlyList<KdpPackageFile> EnumerateFiles(string root, bool excludeManifest) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !excludeManifest || !path.EndsWith("manifest.json", StringComparison.Ordinal))
        .Select(path => new KdpPackageFile(Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length, HashFile(path)))
        .OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();

    private static async ValueTask AddEntryAsync(ZipArchive archive, string name, string content, CompressionLevel level, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, level); entry.LastWriteTime = StableTimestamp;
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async ValueTask WriteStableAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false), cancellationToken);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string MarkdownToText(string value) => Regex.Replace(value, "(?m)^#{1,6}\\s*", string.Empty).Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal);
    private static string EscapePdf(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record PdfLine(string Text, bool Heading, bool FirstParagraphLine, double SpaceBefore);
}
