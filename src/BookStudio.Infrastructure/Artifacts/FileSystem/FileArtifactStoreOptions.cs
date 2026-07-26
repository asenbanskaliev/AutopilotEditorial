namespace BookStudio.Infrastructure.Artifacts.FileSystem;

/// <summary>Filesystem artifact-store limits and canonical workspace paths.</summary>
public sealed record FileArtifactStoreOptions(
    string WorkspaceRoot,
    string StoreRoot,
    long MaximumArtifactBytes,
    int BufferSize)
{
    public static FileArtifactStoreOptions Create(
        string workspaceRoot,
        long maximumArtifactBytes = 256L * 1024L * 1024L,
        int bufferSize = 81920)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (maximumArtifactBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        }
        if (bufferSize < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        var canonicalWorkspace = Path.GetFullPath(workspaceRoot);
        var storeRoot = Path.Combine(canonicalWorkspace, ".bookstudio", "artifacts");
        return new FileArtifactStoreOptions(
            canonicalWorkspace,
            Path.GetFullPath(storeRoot),
            maximumArtifactBytes,
            bufferSize);
    }
}
