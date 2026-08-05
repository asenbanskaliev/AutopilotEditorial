using System.IO.Compression;
using System.Text;
using BookStudio.Autopilot.EditorialJourney;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-vs145-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var validChapter = "# Capítulo 1\n\n—¿Dónde está el expediente?\n\n—En el archivo municipal —respondió Ana.\n\nLa archivera abrió *Diario de Navarra* y comparó las fechas.\n\nAsunto 17: inventario de documentos reservados.";
    var request = new KdpPackageRequest(
        "vs145-professional-book",
        root,
        6m,
        9m,
        0.6m,
        new KdpMetadata(
            "El archivo de las ausencias",
            "Asen Bansk",
            "es-ES",
            "Una novela de misterio documental ambientada en Pamplona sobre memoria pública, archivos y responsabilidad familiar.",
            ["FICTION / Mystery & Detective / General"],
            ["misterio", "Pamplona", "archivo", "memoria"]),
        [new KdpChapter(1, "La señal", validChapter + "\n\n" + string.Join(' ', Enumerable.Repeat("La investigación avanzó sin borrar ninguna evidencia y mantuvo la cronología verificable.", 80)))],
        new KdpCoverInput(1800, 2700, 300, "image/jpeg", new string('a', 64)));

    var parsed = ProfessionalPrintInterior.Parse(validChapter);
    Require(parsed.Any(x => x.Kind == PrintBlockKind.Dialogue), "dialogue block was not parsed");
    Require(parsed.Any(x => x.Inlines.Any(i => i.Italic && i.Text == "Diario de Navarra")), "italics were not parsed semantically");
    Require(parsed.Any(x => x.Kind == PrintBlockKind.Document), "document block was not recognized");

    var shortHyphen = request with
    {
        ProjectId = "vs145-invalid-dialogue",
        Chapters = [new KdpChapter(1, "La señal", validChapter.Replace("—¿Dónde", "-¿Dónde", StringComparison.Ordinal))]
    };
    var blocked = await new ProfessionalKdpProductionPackageBuilder().BuildAsync(shortHyphen);
    Require(!blocked.Passed && blocked.BlockingReasons.Contains("short_dialogue_hyphen_detected"), "short dialogue hyphen was not blocked");

    var result = await new ProfessionalKdpProductionPackageBuilder().BuildAsync(request);
    Require(result.Passed, "professional package failed: " + string.Join(',', result.BlockingReasons));
    Require(File.Exists(result.PackageZip), "professional package zip missing");

    using var archive = ZipFile.OpenRead(result.PackageZip);
    Require(archive.GetEntry("professional-print-audit.json") is not null, "print audit evidence missing");
    var pdf = archive.GetEntry("print-interior.pdf") ?? throw new InvalidOperationException("professional PDF missing");
    using var reader = new StreamReader(pdf.Open(), Encoding.Latin1);
    var raw = await reader.ReadToEndAsync();
    Require(raw.Contains("Times-Italic", StringComparison.Ordinal), "italic font resource missing");
    Require(!raw.Contains("*Diario de Navarra*", StringComparison.Ordinal), "visible Markdown leaked into PDF");
    Require(raw.Contains("Diario de Navarra", StringComparison.Ordinal), "italic text missing from PDF");
    Require(raw.Contains("Copyright", StringComparison.Ordinal), "copyright page missing");
    Require(raw.Contains("El archivo de las ausencias", StringComparison.Ordinal), "title page missing");
    Require(raw.Contains("\u0097¿Dónde está el expediente?", StringComparison.Ordinal), "WinAnsi Spanish dialogue dash was not preserved");
    Require(System.Text.RegularExpressions.Regex.Matches(raw, @"/Type /Page\b").Count >= 3, "front matter and chapter pages missing");
    Console.WriteLine("PASS VS-145 professional print interior and visual quality gate");
}
finally
{
    Directory.Delete(root, true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
