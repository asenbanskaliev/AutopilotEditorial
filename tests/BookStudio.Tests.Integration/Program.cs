using BookStudio.Tests.Integration;
using Microsoft.Data.Sqlite;

var workspaceRoot = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Integration",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspaceRoot);

try
{
    await SqliteJourney.RunAsync(workspaceRoot);
    Console.WriteLine("SQLite integration PASS: lifecycle, dynamic migrations, WAL, serialized writes, rollback, integrity and backup verified.");

    await DeepBookProofIntegrationSmoke.RunAsync(workspaceRoot);
    Console.WriteLine("VS-122 deep no-command proof PASS: durable restart, bounded repair, exact artifacts and publication readiness verified.");

    await PublicationArtifactPipelineIntegrationSmoke.RunAsync(workspaceRoot);
    Console.WriteLine("VS-123 provider-backed artifacts PASS: real EPUB, PDF, DOCX and KDP bytes, provenance, budget and replay verified.");

    await ProviderBackedDeepBookProofAuthoritySmoke.RunAsync(workspaceRoot);
    Console.WriteLine("VS-123 provider-backed deep proof PASS: natural-language journey, real provider artifacts, checkpoint registration and restart idempotency verified.");

    await ImageProviderRightsPipelineSmoke.RunAsync(workspaceRoot);
    Console.WriteLine("VS-124 image provider rights PASS: exact image bytes, provenance, licensing, accessibility, bounded repair and restart reuse verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Integration FAIL: " + exception);
    return 1;
}
finally
{
    SqliteConnection.ClearAllPools();
    TryDeleteDirectory(workspaceRoot);
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
        // Cleanup is best effort after all pooled connections have been cleared.
    }
    catch (UnauthorizedAccessException)
    {
        // Cleanup is best effort after all pooled connections have been cleared.
    }
}