using System.IO.Compression;
using System.Security.Cryptography;
using BookStudio.Autopilot.EditorialJourney;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-vs134-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var chapterText = "# Capítulo 1\n\n" + string.Join(' ', Enumerable.Repeat("La archivera avanza por el expediente, contrasta fechas y conserva la continuidad narrativa.", 45));
    var request = new KdpPackageRequest(
        "vs134-book",
        root,
        6m,
        9m,
        0.5m,
        new KdpMetadata("El archivo de las ausencias", "Autora de prueba", "es-ES", "Una novela de misterio documental ambientada en Pamplona que explora memoria, familia y responsabilidad pública.", ["FICTION / Mystery & Detective / General"], ["misterio", "Pamplona", "archivo", "familia"]),
        [new KdpChapter(1, "El expediente", chapterText), new KdpChapter(2, "La ausencia", chapterText.Replace("Capítulo 1", "Capítulo 2", StringComparison.Ordinal))],
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
    Require(archive.GetEntry("print-interior.pdf")!.Length > 500, "PDF too small");
    using var epubStream = archive.GetEntry("ebook.epub")!.Open();
    using var epubCopy = new MemoryStream();
    await epubStream.CopyToAsync(epubCopy);
    epubCopy.Position = 0;
    using var epub = new ZipArchive(epubCopy, ZipArchiveMode.Read);
    Require(epub.GetEntry("mimetype") is not null && epub.GetEntry("OEBPS/content.opf") is not null && epub.GetEntry("OEBPS/nav.xhtml") is not null, "EPUB structure invalid");

    var invalid = await builder.BuildAsync(request with { ProjectId = "vs134-bad", Cover = request.Cover with { Dpi = 72 } });
    Require(!invalid.Passed && invalid.BlockingReasons.Contains("cover_resolution_insufficient"), "low-resolution cover was not blocked");
    Console.WriteLine("PASS VS-134 reproducible KDP package");
}
finally
{
    Directory.Delete(root, true);
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
