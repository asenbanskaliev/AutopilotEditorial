namespace BookStudio.Application.Artifacts;

/// <summary>Requests publication of exactly one immutable artifact version.</summary>
public sealed record ArtifactWriteRequest(
    string ArtifactId,
    int ExpectedVersion,
    string MediaType,
    Stream Content);
