using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed class ProfessionalKdpProductionPackageBuilder
{
    private static readonly DateTimeOffset StableTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async ValueTask<KdpPackageResult> BuildAsync(KdpPackageRequest request, CancellationToken cancellationToken = default)
    {
        var sourceAudit = ProfessionalPrintInterior.AuditSource(request);
        if (!sourceAudit.Passed)
            return new KdpPackageResult(string.Empty, [], string.Empty, false, sourceAudit.BlockingReasons);

        var baseline = await new KdpProductionPackageBuilder().BuildAsync(request, cancellationToken);
        if (!baseline.Passed) return baseline;

        var output = Path.GetFullPath(request.OutputDirectory);
        var staging = Path.Combine(output, request.ProjectId + ".kdp");
        var pdfPath = Path.Combine(staging, "print-interior.pdf");
        await File.WriteAllBytesAsync(pdfPath, ProfessionalPrintInterior.BuildPdf(request), cancellationToken);

        var pdfText = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(pdfPath, cancellationToken));
        var audit = BuildArtifactAudit(request, pdfText);
        if (!audit.Passed)
            return new KdpPackageResult(string.Empty, [], string.Empty, false, audit.BlockingReasons);

        var auditPath = Path.Combine(staging, "professional-print-audit.json");
        await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(audit, JsonOptions), new UTF8Encoding(false), cancellationToken);
        File.SetLastWriteTimeUtc(auditPath, StableTimestamp.UtcDateTime);
        File.SetLastWriteTimeUtc(pdfPath, StableTimestamp.UtcDateTime);

        var filesWithoutManifest = EnumerateFiles(staging, excludeManifest: true);
        var manifest = new
        {
            schemaVersion = 2,
            request.ProjectId,
            generatedAt = "2026-01-01T00:00:00Z",
            professionalPrintInterior = true,
            trim = new { widthInches = request.TrimWidthInches, heightInches = request.TrimHeightInches, marginInches = request.MarginInches },
            files = filesWithoutManifest,
        };
        var manifestPath = Path.Combine(staging, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false), cancellationToken);
        File.SetLastWriteTimeUtc(manifestPath, StableTimestamp.UtcDateTime);

        var allFiles = EnumerateFiles(staging, excludeManifest: false);
        var manifestSha = HashFile(manifestPath);
        var zipPath = Path.Combine(output, request.ProjectId + ".kdp.zip");
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

    private static ProfessionalPrintAudit BuildArtifactAudit(KdpPackageRequest request, string pdf)
    {
        var reasons = new List<string>();
        var visibleMarkdown = Count(pdf, "*") / 2;
        var shortHyphens = System.Text.RegularExpressions.Regex.Matches(pdf, @"\(-[\p{L}¿¡]").Count;
        var pageCount = System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type /Page\b").Count;
        var hasTitle = pdf.Contains(request.Metadata.Title, StringComparison.Ordinal);
        var hasCopyright = pdf.Contains("Copyright", StringComparison.Ordinal);
        var hasFolios = pdf.Contains(" 9 Tf", StringComparison.Ordinal);
        var deterministicFonts = pdf.Contains("/Times-Roman", StringComparison.Ordinal) && pdf.Contains("/Times-Italic", StringComparison.Ordinal);
        if (visibleMarkdown > 0) reasons.Add("visible_markdown_in_pdf");
        if (shortHyphens > 0) reasons.Add("short_dialogue_hyphen_in_pdf");
        if (!hasTitle) reasons.Add("title_page_missing");
        if (!hasCopyright) reasons.Add("copyright_page_missing");
        if (!hasFolios) reasons.Add("page_numbers_missing");
        if (!deterministicFonts) reasons.Add("deterministic_font_resources_missing");
        if (pageCount < request.Chapters.Count + 2) reasons.Add("chapter_or_front_matter_pages_missing");
        return new ProfessionalPrintAudit(reasons.Count == 0, reasons, pageCount, visibleMarkdown, shortHyphens, 0, hasTitle, hasCopyright, hasFolios, deterministicFonts);
    }

    private static int Count(string value, string token)
    {
        var count = 0; var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0) { count++; index += token.Length; }
        return count;
    }

    private static IReadOnlyList<KdpPackageFile> EnumerateFiles(string root, bool excludeManifest) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !excludeManifest || !string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.Ordinal))
            .Select(path => new KdpPackageFile(Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length, HashFile(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
