using BookStudio.Tests.Integration;

var workspaceRoot = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Artifacts",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspaceRoot);

try
{
    await ArtifactStoreJourney.RunAsync(workspaceRoot);
    Console.WriteLine("Artifact store integration PASS: immutable ingest, dedupe, conflicts, cancellation, confinement and tamper detection verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Artifact store integration FAIL: " + exception);
    return 1;
}
finally
{
    TryDelete(workspaceRoot);
}

static void TryDelete(string path)
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
        // Test cleanup is best effort after all streams have been disposed.
    }
    catch (UnauthorizedAccessException)
    {
        // Test cleanup is best effort after all streams have been disposed.
    }
}
