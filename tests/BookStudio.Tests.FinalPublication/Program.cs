using System.Text;
using BookStudio.Autopilot.EditorialJourney;

var output = Path.Combine(Path.GetTempPath(), "bookstudio-vs142-" + Guid.NewGuid().ToString("N"));
var artifacts = new[]
{
    new PublicationArtifact("manuscript.epub", "application/epub+zip", Encoding.UTF8.GetBytes("EPUB")),
    new PublicationArtifact("interior.pdf", "application/pdf", Encoding.UTF8.GetBytes("PDF interior")),
    new PublicationArtifact("cover.pdf", "application/pdf", Encoding.UTF8.GetBytes("PDF cover")),
    new PublicationArtifact("metadata.json", "application/json", Encoding.UTF8.GetBytes("{\"title\":\"Book\"}")),
    new PublicationArtifact("publication-checklist.md", "text/markdown", Encoding.UTF8.GetBytes("- [x] reviewed")),
    new PublicationArtifact("manuscript.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Encoding.UTF8.GetBytes("DOCX")),
};
var journey = new FinalPublicationJourney();
var result = await journey.BuildAsync("book-142", artifacts, output, CancellationToken.None);
FinalPublicationJourney.Verify(result);
Require(result.Entries.Count == artifacts.Length, "manifest entry count mismatch");
Require(new FileInfo(result.ZipPath).Length > 0, "ZIP was empty");
await RequireThrowsAsync(() => journey.BuildAsync("bad", artifacts.Where(x => x.Name != "cover.pdf").ToArray(), output, CancellationToken.None), "missing cover accepted");
await RequireThrowsAsync(() => journey.BuildAsync("duplicate", artifacts.Concat([artifacts[0]]).ToArray(), output, CancellationToken.None), "duplicate artifact accepted");
Directory.Delete(output, true);
Console.WriteLine("PASS VS-142 final download and publication journey");

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task RequireThrowsAsync(Func<ValueTask<PublicationPackageResult>> action, string message)
{
    try { await action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(message);
}
