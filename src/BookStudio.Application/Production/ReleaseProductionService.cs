using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookStudio.Application.Artifacts;

namespace BookStudio.Application.Production;

/// <summary>Prepares canonical immutable release manifests and verifies every referenced source.</summary>
public sealed partial class ReleaseProductionService : IReleaseProductionService
{
    public const int MaximumManifestBytes = 1024 * 1024;
    public const string ReleaseMediaType = "application/vnd.bookstudio.release-manifest+json";
    public const string SchemaVersion = "1.0.0";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
    };

    private static readonly IReadOnlySet<string> AllowedRoles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "manuscript",
            "cover",
            "metadata",
            "interior-pdf",
            "epub",
            "supplemental",
        };

    private readonly IArtifactStore _store;

    public ReleaseProductionService(IArtifactStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<ReleasePreparationResult> PrepareAsync(
        ReleasePreparationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateProjectId(command.ProjectId);
        ValidateReleaseId(command.ReleaseId);
        if (command.ExpectedVersion < 1)
        {
            throw new ReleaseProductionException("invalid_version", "Expected release version must be positive.");
        }
        if (string.IsNullOrWhiteSpace(command.Title) ||
            command.Title.Length > 200 ||
            command.Title.Any(char.IsControl))
        {
            throw new ReleaseProductionException("invalid_title", "Release title must contain 1 to 200 safe characters.");
        }
        if (string.IsNullOrWhiteSpace(command.Language) || !LanguageRegex().IsMatch(command.Language))
        {
            throw new ReleaseProductionException("invalid_language", "Release language must be a supported BCP-47 form.");
        }
        if (command.Sources is null || command.Sources.Count is < 1 or > 50)
        {
            throw new ReleaseProductionException("invalid_sources", "Release requires between 1 and 50 sources.");
        }

        var releaseArtifactId = $"{command.ProjectId}.release.{command.ReleaseId}";
        var normalized = command.Sources
            .Select(source => NormalizeSourceRequest(command.ProjectId, releaseArtifactId, source))
            .OrderBy(source => source.Role, StringComparer.Ordinal)
            .ThenBy(source => source.ArtifactId, StringComparer.Ordinal)
            .ThenBy(source => source.Version)
            .ToArray();
        if (normalized.Count(source => source.Role == "manuscript") != 1)
        {
            throw new ReleaseProductionException("invalid_manuscript_count", "Release requires exactly one manuscript source.");
        }
        if (normalized.Select(SourceIdentity).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ReleaseProductionException("duplicate_source", "Release source references must be unique.");
        }

        var sources = new List<ReleaseManifestSource>(normalized.Length);
        foreach (var source in normalized)
        {
            var manifest = await ReadVerifiedManifestAsync(
                    source.ArtifactId,
                    source.Version,
                    cancellationToken)
                .ConfigureAwait(false);
            sources.Add(new ReleaseManifestSource(
                source.Role,
                manifest.ArtifactId,
                manifest.Version,
                manifest.Sha256,
                manifest.Length,
                manifest.MediaType));
        }

        var document = new ReleaseManifestDocument(
            SchemaVersion,
            command.ProjectId,
            command.ReleaseId,
            command.Title.Trim(),
            command.Language,
            sources);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumManifestBytes)
        {
            throw new ReleaseProductionException("release_manifest_too_large", "Release manifest exceeds its bounded size.");
        }

        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            var manifest = await _store.PutAsync(
                    new ArtifactWriteRequest(
                        releaseArtifactId,
                        command.ExpectedVersion,
                        ReleaseMediaType,
                        stream),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ReleasePreparationResult(
                ToReference(command.ProjectId, manifest),
                document);
        }
        catch (ArtifactVersionConflictException exception)
        {
            throw new ReleaseProductionException(
                "release_version_conflict",
                $"Release version conflict. Required version is {exception.RequiredVersion.ToString(CultureInfo.InvariantCulture)}.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new ReleaseProductionException("artifact_integrity_failed", "Release manifest storage integrity verification failed.");
        }
        catch (ArgumentException)
        {
            throw new ReleaseProductionException("invalid_release", "Release preparation parameters are invalid.");
        }
    }

    public async ValueTask<ReleasePreflightResult> RunPreflightAsync(
        ReleasePreflightQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateProjectId(query.ProjectId);
        if (string.IsNullOrWhiteSpace(query.ReleaseArtifactId) ||
            !ArtifactIdRegex().IsMatch(query.ReleaseArtifactId) ||
            !query.ReleaseArtifactId.StartsWith(query.ProjectId + ".release.", StringComparison.Ordinal))
        {
            throw new ReleaseProductionException("release_scope_violation", "Release artifact does not belong to the requested project scope.");
        }
        if (query.Version < 1)
        {
            throw new ReleaseProductionException("invalid_version", "Release version must be positive.");
        }
        if (!string.Equals(query.Profile, "release-basic", StringComparison.Ordinal))
        {
            throw new ReleaseProductionException("unknown_preflight_profile", "Only the release-basic preflight profile is available.");
        }

        var (releaseManifest, document) = await ReadReleaseDocumentAsync(
                query.ReleaseArtifactId,
                query.Version,
                cancellationToken)
            .ConfigureAwait(false);
        var checks = new List<ReleasePreflightCheck>();
        checks.Add(Check(
            "release.schema_version",
            string.Equals(document.SchemaVersion, SchemaVersion, StringComparison.Ordinal),
            string.Equals(document.SchemaVersion, SchemaVersion, StringComparison.Ordinal) ? 1 : 0,
            1,
            "Release manifest must use the supported schema version."));

        var scopeValid = string.Equals(document.ProjectId, query.ProjectId, StringComparison.Ordinal) &&
                         query.ReleaseArtifactId == $"{query.ProjectId}.release.{document.ReleaseId}";
        checks.Add(Check(
            "release.project_scope",
            scopeValid,
            scopeValid ? 1 : 0,
            1,
            "Release manifest must match the requested project and release artifact."));

        var manuscriptCount = document.Sources.Count(source => source.Role == "manuscript");
        checks.Add(Check(
            "release.manuscript_present",
            manuscriptCount == 1,
            manuscriptCount,
            1,
            "Release must contain exactly one manuscript source."));

        var uniqueCount = document.Sources.Select(SourceIdentity).Distinct(StringComparer.Ordinal).Count();
        checks.Add(Check(
            "release.no_duplicate_sources",
            uniqueCount == document.Sources.Count,
            document.Sources.Count - uniqueCount,
            0,
            "Release sources must not contain duplicate role/artifact/version references."));

        var unavailable = 0;
        var integrityFailures = 0;
        var incompatible = 0;
        foreach (var source in document.Sources)
        {
            if (!AllowedRoles.Contains(source.Role) ||
                !source.ArtifactId.StartsWith(query.ProjectId + ".", StringComparison.Ordinal) ||
                source.ArtifactId == query.ReleaseArtifactId ||
                source.Version < 1)
            {
                unavailable++;
                continue;
            }

            try
            {
                var actual = await ReadVerifiedManifestAsync(
                        source.ArtifactId,
                        source.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(actual.Sha256, source.Sha256, StringComparison.Ordinal) ||
                    actual.Length != source.Length ||
                    !string.Equals(actual.MediaType, source.MediaType, StringComparison.Ordinal))
                {
                    integrityFailures++;
                }
                if (!IsMediaCompatible(source.Role, actual.MediaType))
                {
                    incompatible++;
                }
            }
            catch (ReleaseProductionException exception) when (
                exception.Code is "source_not_found" or "invalid_source")
            {
                unavailable++;
            }
            catch (ReleaseProductionException exception) when (
                exception.Code == "source_integrity_failed")
            {
                integrityFailures++;
            }
        }

        checks.Add(Check(
            "release.sources_available",
            unavailable == 0,
            unavailable,
            0,
            "All release sources must exist and belong to the project."));
        checks.Add(Check(
            "release.sources_integrity",
            integrityFailures == 0,
            integrityFailures,
            0,
            "All source hashes, lengths and media types must match verified artifacts."));
        checks.Add(Check(
            "release.role_media_compatibility",
            incompatible == 0,
            incompatible,
            0,
            "Every source media type must be compatible with its production role."));

        var reasons = checks
            .Where(check => string.Equals(check.Status, "fail", StringComparison.Ordinal))
            .Select(check => check.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new ReleasePreflightResult(
            query.Profile,
            reasons.Length == 0 ? "PASS" : "BLOCKED",
            ToReference(query.ProjectId, releaseManifest),
            document,
            checks,
            reasons);
    }

    public static string BuildOperationId(params string[] parts)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private async ValueTask<(ArtifactManifest Manifest, ReleaseManifestDocument Document)> ReadReleaseDocumentAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _store.GetManifestAsync(artifactId, version, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(manifest.MediaType, ReleaseMediaType, StringComparison.Ordinal))
            {
                throw new ReleaseProductionException("invalid_release_media_type", "Artifact is not a BookStudio release manifest.");
            }
            if (manifest.Length > MaximumManifestBytes)
            {
                throw new ReleaseProductionException("release_manifest_too_large", "Release manifest exceeds its bounded size.");
            }

            await using var stream = await _store.OpenReadAsync(
                    artifactId,
                    version,
                    verifyIntegrity: true,
                    cancellationToken)
                .ConfigureAwait(false);
            using var memory = new MemoryStream((int)manifest.Length);
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            if (memory.Length != manifest.Length || memory.Length > MaximumManifestBytes)
            {
                throw new ReleaseProductionException("artifact_integrity_failed", "Release manifest length verification failed.");
            }

            ReleaseManifestDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<ReleaseManifestDocument>(memory.ToArray(), JsonOptions);
            }
            catch (JsonException)
            {
                throw new ReleaseProductionException("invalid_release_manifest", "Release manifest JSON is invalid.");
            }
            if (document is null ||
                string.IsNullOrWhiteSpace(document.ProjectId) ||
                string.IsNullOrWhiteSpace(document.ReleaseId) ||
                string.IsNullOrWhiteSpace(document.Title) ||
                string.IsNullOrWhiteSpace(document.Language) ||
                document.Sources is null ||
                document.Sources.Count is < 1 or > 50)
            {
                throw new ReleaseProductionException("invalid_release_manifest", "Release manifest structure is invalid.");
            }
            return (manifest, document);
        }
        catch (ReleaseProductionException)
        {
            throw;
        }
        catch (ArtifactNotFoundException)
        {
            throw new ReleaseProductionException("release_not_found", "The requested release manifest was not found.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new ReleaseProductionException("artifact_integrity_failed", "Release manifest integrity verification failed.");
        }
        catch (ArgumentException)
        {
            throw new ReleaseProductionException("invalid_release", "Release reference is invalid.");
        }
    }

    private async ValueTask<ArtifactManifest> ReadVerifiedManifestAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _store.GetManifestAsync(artifactId, version, cancellationToken)
                .ConfigureAwait(false);
            await using var stream = await _store.OpenReadAsync(
                    artifactId,
                    version,
                    verifyIntegrity: true,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            return manifest;
        }
        catch (ArtifactNotFoundException)
        {
            throw new ReleaseProductionException("source_not_found", "A release source was not found.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new ReleaseProductionException("source_integrity_failed", "A release source failed integrity verification.");
        }
        catch (ArgumentException)
        {
            throw new ReleaseProductionException("invalid_source", "A release source reference is invalid.");
        }
    }

    private static ReleaseSourceRequest NormalizeSourceRequest(
        string projectId,
        string releaseArtifactId,
        ReleaseSourceRequest source)
    {
        if (source is null ||
            string.IsNullOrWhiteSpace(source.Role) ||
            !AllowedRoles.Contains(source.Role) ||
            string.IsNullOrWhiteSpace(source.ArtifactId) ||
            !ArtifactIdRegex().IsMatch(source.ArtifactId) ||
            !source.ArtifactId.StartsWith(projectId + ".", StringComparison.Ordinal) ||
            source.ArtifactId == releaseArtifactId ||
            source.Version < 1)
        {
            throw new ReleaseProductionException("invalid_source", "Release source role, scope or version is invalid.");
        }
        return new ReleaseSourceRequest(source.Role, source.ArtifactId, source.Version);
    }

    private static bool IsMediaCompatible(string role, string mediaType) =>
        role switch
        {
            "manuscript" => mediaType is "text/markdown" or "text/plain",
            "cover" => mediaType is "image/png" or "image/jpeg" or "image/svg+xml",
            "metadata" => mediaType == "application/json",
            "interior-pdf" => mediaType == "application/pdf",
            "epub" => mediaType == "application/epub+zip",
            "supplemental" => !string.IsNullOrWhiteSpace(mediaType),
            _ => false,
        };

    private static ReleasePreflightCheck Check(
        string id,
        bool passing,
        int observed,
        int threshold,
        string message) =>
        new(id, passing ? "pass" : "fail", observed, threshold, message);

    private static string SourceIdentity(ReleaseSourceRequest source) =>
        $"{source.Role}|{source.ArtifactId}|{source.Version.ToString(CultureInfo.InvariantCulture)}";

    private static string SourceIdentity(ReleaseManifestSource source) =>
        $"{source.Role}|{source.ArtifactId}|{source.Version.ToString(CultureInfo.InvariantCulture)}";

    private static void ValidateProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !ProjectIdRegex().IsMatch(projectId))
        {
            throw new ReleaseProductionException("invalid_project_id", "Project ID is invalid.");
        }
    }

    private static void ValidateReleaseId(string releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId) || !ReleaseIdRegex().IsMatch(releaseId))
        {
            throw new ReleaseProductionException("invalid_release_id", "Release ID is invalid.");
        }
    }

    private static ReleaseArtifactReference ToReference(
        string projectId,
        ArtifactManifest manifest) =>
        new(
            manifest.ArtifactId,
            manifest.Version,
            manifest.Sha256,
            manifest.Length,
            manifest.MediaType,
            $"book://project/{projectId}/artifact/{manifest.ArtifactId}/versions/{manifest.Version.ToString(CultureInfo.InvariantCulture)}");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIdRegex();

    [GeneratedRegex("^[a-z]{2,3}(?:-[A-Z][A-Za-z0-9]{1,7})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageRegex();
}
