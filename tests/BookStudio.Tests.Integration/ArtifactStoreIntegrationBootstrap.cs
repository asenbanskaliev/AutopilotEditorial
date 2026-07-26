using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace BookStudio.Tests.Integration;

internal static class ArtifactStoreIntegrationBootstrap
{
    [ModuleInitializer]
    internal static void RunArtifactStoreJourney()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "BookStudio.Tests.ArtifactStore",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ArtifactStoreJourney.RunAsync(root).GetAwaiter().GetResult();
            Console.WriteLine("Artifact store integration PASS: immutable ingest, dedupe, conflicts, cancellation, confinement and tamper detection verified.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
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
}
