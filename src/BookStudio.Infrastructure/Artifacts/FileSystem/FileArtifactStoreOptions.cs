namespace BookStudio.Infrastructure.Artifacts.FileSystem;

/// <summary>Filesystem artifact-store limits and canonical workspace paths.</summary>
public sealed record FileArtifactStoreOptions(
    string WorkspaceRoot,
    string StoreRoot,
    long MaximumArtifactBytes,
    long MaximumStoreBytes,
    int MaximumStoreFiles,
    int BufferSize)
{
    public const long DefaultMaximumArtifactBytes = 256L * 1024L * 1024L;
    public const long DefaultMaximumStoreBytes = 4L * 1024L * 1024L * 1024L;
    public const int DefaultMaximumStoreFiles = 250000;

    public static FileArtifactStoreOptions Create(
        string workspaceRoot,
        long maximumArtifactBytes = DefaultMaximumArtifactBytes,
        long maximumStoreBytes = DefaultMaximumStoreBytes,
        int maximumStoreFiles = DefaultMaximumStoreFiles,
        int bufferSize = 81920)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (maximumArtifactBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        }
        if (maximumStoreBytes < maximumArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumStoreBytes),
                "Store byte quota must be greater than or equal to the artifact limit.");
        }
        if (maximumStoreFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStoreFiles));
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
            maximumStoreBytes,
            maximumStoreFiles,
            bufferSize);
    }
}
