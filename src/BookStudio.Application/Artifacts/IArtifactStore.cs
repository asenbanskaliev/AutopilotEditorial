namespace BookStudio.Application.Artifacts;

/// <summary>Provider-neutral immutable artifact storage.</summary>
public interface IArtifactStore : IAsyncDisposable
{
    ValueTask<ArtifactManifest> PutAsync(
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ArtifactManifest> GetManifestAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(
        string artifactId,
        int version,
        bool verifyIntegrity = true,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ArtifactManifest>> ListVersionsAsync(
        string artifactId,
        CancellationToken cancellationToken = default);

    ValueTask VerifyAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken = default);
}

public class ArtifactStoreException : Exception
{
    public ArtifactStoreException(string message) : base(message) { }
    public ArtifactStoreException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ArtifactVersionConflictException : ArtifactStoreException
{
    public ArtifactVersionConflictException(string artifactId, int requestedVersion, int requiredVersion)
        : base($"Artifact '{artifactId}' requested version {requestedVersion}, but required version is {requiredVersion}.")
    {
        ArtifactId = artifactId;
        RequestedVersion = requestedVersion;
        RequiredVersion = requiredVersion;
    }

    public string ArtifactId { get; }
    public int RequestedVersion { get; }
    public int RequiredVersion { get; }
}

public sealed class ArtifactIntegrityException : ArtifactStoreException
{
    public ArtifactIntegrityException(string message) : base(message) { }
    public ArtifactIntegrityException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ArtifactNotFoundException : ArtifactStoreException
{
    public ArtifactNotFoundException(string artifactId, int version)
        : base($"Artifact '{artifactId}' version {version} was not found.") { }
}

public sealed class ArtifactSizeLimitExceededException : ArtifactStoreException
{
    public ArtifactSizeLimitExceededException(long maximumLength)
        : base($"Artifact content exceeded the maximum length of {maximumLength} bytes.")
    {
        MaximumLength = maximumLength;
    }

    public long MaximumLength { get; }
}
