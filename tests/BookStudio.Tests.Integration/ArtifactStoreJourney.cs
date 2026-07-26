using System.Text;
using BookStudio.Application.Artifacts;
using BookStudio.Infrastructure.Artifacts.FileSystem;

namespace BookStudio.Tests.Integration;

internal static class ArtifactStoreJourney
{
    public static async Task RunAsync(string parentWorkspaceRoot)
    {
        var workspaceRoot = Path.Combine(parentWorkspaceRoot, "artifact-store");
        Directory.CreateDirectory(workspaceRoot);
        var options = FileArtifactStoreOptions.Create(
            workspaceRoot,
            maximumArtifactBytes: 1024 * 1024,
            bufferSize: 4096);
        await using var store = new FileArtifactStore(options);

        await RequireThrowsAsync<ArgumentException>(
            () => PutTextAsync(store, "../escape", 1, "invalid"));

        var first = await PutTextAsync(store, "chapter-draft", 1, "same-content");
        Require(first.Version == 1, "The first artifact version must be one.");
        Require(first.Length == Encoding.UTF8.GetByteCount("same-content"), "Manifest length mismatch.");

        await using (var stream = await store.OpenReadAsync("chapter-draft", 1, verifyIntegrity: true))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false))
        {
            Require(await reader.ReadToEndAsync() == "same-content", "Verified artifact content mismatch.");
        }

        var deduplicated = await PutTextAsync(store, "audit-copy", 1, "same-content");
        Require(first.Sha256 == deduplicated.Sha256, "Identical content must share a content hash.");
        var blobRoot = Path.Combine(options.StoreRoot, "blobs", "sha256");
        Require(
            Directory.EnumerateFiles(blobRoot, "*", SearchOption.AllDirectories).Count() == 1,
            "Identical content must deduplicate to one blob.");

        await RequireThrowsAsync<ArtifactVersionConflictException>(
            () => PutTextAsync(store, "chapter-draft", 1, "replacement-forbidden"));
        var second = await PutTextAsync(store, "chapter-draft", 2, "second-version");
        Require(second.Version == 2, "The second immutable version was not published.");
        var versions = await store.ListVersionsAsync("chapter-draft");
        Require(
            versions.Select(item => item.Version).SequenceEqual([1, 2]),
            "Artifact versions must be listed in ascending order.");

        var raceResults = await Task.WhenAll(
            TryPutTextAsync(store, "concurrent-artifact", 1, "writer-a"),
            TryPutTextAsync(store, "concurrent-artifact", 1, "writer-b"));
        Require(raceResults.Count(result => result == PutResult.Success) == 1, "Exactly one concurrent writer must publish.");
        Require(raceResults.Count(result => result == PutResult.VersionConflict) == 1, "The losing writer must receive a version conflict.");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => PutTextAsync(store, "cancelled-artifact", 1, "not-published", cancellation.Token));
        }
        Require((await store.ListVersionsAsync("cancelled-artifact")).Count == 0, "Cancelled publication must not create a manifest.");

        await using (var limitedStore = new FileArtifactStore(
                         FileArtifactStoreOptions.Create(workspaceRoot, maximumArtifactBytes: 4, bufferSize: 4096)))
        {
            await RequireThrowsAsync<ArtifactSizeLimitExceededException>(
                () => PutTextAsync(limitedStore, "oversized-artifact", 1, "12345"));
        }
        Require((await store.ListVersionsAsync("oversized-artifact")).Count == 0, "Oversized publication must not create a manifest.");

        var tempRoot = Path.Combine(options.StoreRoot, "temp");
        Require(!Directory.EnumerateFiles(tempRoot).Any(), "Temporary files must be cleaned after success and failure.");

        var manifestTamper = await PutTextAsync(store, "manifest-tamper", 1, "manifest-protected");
        var manifestPath = Path.Combine(options.StoreRoot, "manifests", manifestTamper.ArtifactId, "1.json");
        await File.WriteAllTextAsync(manifestPath, "{not-json", Encoding.UTF8);
        await RequireThrowsAsync<ArtifactIntegrityException>(
            async () => _ = await store.GetManifestAsync("manifest-tamper", 1));

        var blobTamper = await PutTextAsync(store, "blob-tamper", 1, "blob-protected-unique");
        var blobPath = Path.Combine(
            options.StoreRoot,
            "blobs",
            "sha256",
            blobTamper.Sha256[..2],
            blobTamper.Sha256[2..]);
        await File.WriteAllTextAsync(blobPath, "tampered", Encoding.UTF8);
        await RequireThrowsAsync<ArtifactIntegrityException>(
            async () => await store.VerifyAsync("blob-tamper", 1));

        if (!OperatingSystem.IsWindows())
        {
            VerifySymlinkRejection(parentWorkspaceRoot);
        }

        await store.DisposeAsync();
        await RequireThrowsAsync<ObjectDisposedException>(
            () => PutTextAsync(store, "after-dispose", 1, "rejected"));
    }

    private static async Task<ArtifactManifest> PutTextAsync(
        IArtifactStore store,
        string artifactId,
        int version,
        string content,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        return await store.PutAsync(
            new ArtifactWriteRequest(artifactId, version, "text/plain", stream),
            cancellationToken);
    }

    private static async Task<PutResult> TryPutTextAsync(
        IArtifactStore store,
        string artifactId,
        int version,
        string content)
    {
        try
        {
            _ = await PutTextAsync(store, artifactId, version, content);
            return PutResult.Success;
        }
        catch (ArtifactVersionConflictException)
        {
            return PutResult.VersionConflict;
        }
    }

    private static void VerifySymlinkRejection(string parentWorkspaceRoot)
    {
        var workspace = Path.Combine(parentWorkspaceRoot, "artifact-symlink");
        var storeRoot = Path.Combine(workspace, ".bookstudio", "artifacts");
        var outside = Path.Combine(parentWorkspaceRoot, "artifact-symlink-outside");
        Directory.CreateDirectory(storeRoot);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(storeRoot, "blobs"), outside);
        RequireThrows<ArtifactStoreException>(
            () => _ = new FileArtifactStore(FileArtifactStoreOptions.Create(workspace)));
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void RequireThrows<TException>(Action operation)
        where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private enum PutResult
    {
        Success,
        VersionConflict,
    }
}
