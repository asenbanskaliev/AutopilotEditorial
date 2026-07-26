namespace BookStudio.Application.Artifacts;

/// <summary>Immutable identity and integrity metadata for one artifact version.</summary>
public sealed record ArtifactManifest(
    string SchemaVersion,
    string ArtifactId,
    int Version,
    string Sha256,
    long Length,
    string MediaType,
    DateTimeOffset CreatedAtUtc);
