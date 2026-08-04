using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BookStudio.Autopilot.EditorialJourney;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-vs134-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var firstParagraph = string.Join(' ', Enumerable.Repeat("¿Dónde está el expediente de Íñigo? La archivera revisó la sección pública, encontró una página húmeda y anotó la desaparición.", 28));
    var secondParagraph = string.Join(' ', Enumerable.Repeat("Después cruzó la plaza, habló con su tía y confirmó que ningún registro podía explicar aquella ausencia.", 28));
    var chapterOne = $"# Capítulo 1: La señal\n\n{firstParagraph}\n\n{secondParagraph}";
    var finalMarker = "ÚLTIMA PÁGINA: la investigación concluyó en Pamplona sin borrar la memoria de Íñigo.";
    var chapterTwo = $"# Capítulo 2: La ausencia\n\n{firstParagraph}\n\n{secondParagraph}\n\n{finalMarker}";
    var request = new KdpPackageRequest(
        "vs134-book",
        root,
        6m,
        9m,
        0.5m,
        new KdpMetadata("El archivo de las ausencias", "Autora de prueba", "es-ES", "Una novela de misterio documental ambientada en Pamplona que explora memoria, familia y responsabilidad pública.", ["FICTION / Mystery & Detective / General"], ["misterio", "Pamplona", "archivo", "familia"]),
        [new KdpChapter(1, "La señal", chapterOne), new KdpChapter(2, "La ausencia", chapterTwo)],
        new KdpCoverInput(1800, 2700, 300, "image/jpeg", new string('a', 64)));

    var builder = new KdpProductionPackageBuilder();
    var first = await builder.BuildAsync(request);
    Require(first.Passed, string.Join(',', first.BlockingReasons));
    Require(File.Exists(first.PackageZip), "package zip missing");
    var firstHash = Hash(first.PackageZip);
    var second = await builder.BuildAsync(request);
    Require(second.Passed, "second package failed");
    Require(firstHash == Hash(second.PackageZip), "package was not reproducible");
    Require(first.ManifestSha256 == second.ManifestSha256, "manifest hash changed");

    using var archive = ZipFile.OpenRead(second.PackageZip);
    var names = archive.Entries.Select(x => x.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    foreach (var required in new[] { "ebook.epub", "print-interior.pdf", "metadata.json", "kdp-checklist.json", "cover-input.json", "manifest.json", "manuscript.md" })
        Require(names.Contains(required, StringComparer.Ordinal), $"missing {required}");

    var pdfEntry = archive.GetEntry("print-interior.pdf")!;
    Require(pdfEntry.Length > 5000, "PDF too small for complete manuscript");
    using var pdfStream = pdfEntry.Open();
    using var pdfCopy = new MemoryStream();
    await pdfStream.CopyToAsync(pdfCopy);
    var pdfText = Encoding.Latin1.GetString(pdfCopy.ToArray());
    Require(pdfText.Contains("/Encoding /WinAnsiEncoding", StringComparison.Ordinal), "PDF does not declare Spanish-compatible encoding");
    Require(pdfText.Contains("¿Dónde está el expediente de Íñigo?", StringComparison.Ordinal), "Spanish punctuation or accents were corrupted");
    Require(pdfText.Contains("Capítulo 2: La ausencia", StringComparison.Ordinal), "second chapter was omitted");
    Require(pdfText.Contains(finalMarker, StringComparison.Ordinal), "final paragraph was omitted");
    Require(Regex.Matches(pdfText, @"/Type /Page\b").Count >= 4, "complete manuscript was not paginated across multiple pages");
    Require(pdfText.Contains("BT /F1 10.5 Tf 54 ", StringComparison.Ordinal), "paragraph first-line indentation missing");
    Require(pdfText.Contains("BT /F1 10.5 Tf 36 ", StringComparison.Ordinal), "wrapped paragraph continuation alignment missing");

    using var epubStream = archive.GetEntry("ebook.epub")!.Open();
    using var epubCopy = new MemoryStream();
    await epubStream.CopyToAsync(epubCopy);
    epubCopy.Position = 0;
    using var epub = new ZipArchive(epubCopy, ZipArchiveMode.Read);
    Require(epub.GetEntry("mimetype") is not null && epub.GetEntry("OEBPS/content.opf") is not null && epub.GetEntry("OEBPS/nav.xhtml") is not null, "EPUB structure invalid");
    using var chapterEntry = epub.GetEntry("OEBPS/chapter-001.xhtml")!.Open();
    using var chapterReader = new StreamReader(chapterEntry, Encoding.UTF8);
    var chapterXhtml = await chapterReader.ReadToEndAsync();
    Require(chapterXhtml.Contains("</p><p>", StringComparison.Ordinal), "EPUB paragraph boundaries were flattened");
    Require(chapterXhtml.Contains("Íñigo", StringComparison.Ordinal), "EPUB accents were corrupted");

    var invalid = await builder.BuildAsync(request with { ProjectId = "vs134-bad", Cover = request.Cover with { Dpi = 72 } });
    Require(!invalid.Passed && invalid.BlockingReasons.Contains("cover_resolution_insufficient"), "low-resolution cover was not blocked");
    Console.WriteLine("PASS VS-134 reproducible KDP package with Spanish PDF typography and full pagination");
}
finally
{
    Directory.Delete(root, true);
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
