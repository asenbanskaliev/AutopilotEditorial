using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BookStudio.Application.Artifacts;

/// <summary>Bounded, project-confined read and comparison use cases over immutable artifacts.</summary>
public sealed partial class ArtifactQueryService : IArtifactQueryService
{
    public const int MaximumInlineTextBytes = 256 * 1024;
    public const int MaximumResourceBytes = 1024 * 1024;
    public const int MaximumDiffBytes = 1024 * 1024;
    public const int MaximumDiffLinesPerSide = 500;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IArtifactStore _store;

    public ArtifactQueryService(IArtifactStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<ArtifactGetResult> GetAsync(
        ArtifactGetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(query.ProjectId, query.ArtifactId);
        ValidateVersion(query.Version, nameof(query.Version));

        var manifest = await GetManifestSafeAsync(
                query.ArtifactId,
                query.Version,
                cancellationToken)
            .ConfigureAwait(false);
        var reference = ToReference(query.ProjectId, manifest);
        var warnings = new List<string>();
        string? inlineText = null;

        if (query.IncludeContent)
        {
            if (!IsTextCompatible(manifest.MediaType))
            {
                warnings.Add("Artifact content is not text-compatible and remains available through its resource reference.");
            }
            else if (manifest.Length > MaximumInlineTextBytes)
            {
                warnings.Add($"Artifact text exceeds the {MaximumInlineTextBytes} byte inline-content limit.");
            }
            else
            {
                inlineText = await ReadTextSafeAsync(
                        query.ArtifactId,
                        query.Version,
                        MaximumInlineTextBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new ArtifactGetResult(
            reference,
            inlineText,
            inlineText is not null,
            warnings);
    }

    public async ValueTask<ArtifactCompareResult> CompareAsync(
        ArtifactCompareQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(query.ProjectId, query.ArtifactId);
        ValidateVersion(query.LeftVersion, nameof(query.LeftVersion));
        ValidateVersion(query.RightVersion, nameof(query.RightVersion));
        if (query.LeftVersion == query.RightVersion)
        {
            throw new ArtifactQueryException(
                "invalid_versions",
                "Left and right artifact versions must be different.");
        }
        if (query.MaxDifferences is < 1 or > 100)
        {
            throw new ArtifactQueryException(
                "invalid_max_differences",
                "maxDifferences must be between 1 and 100.");
        }

        var leftManifest = await GetManifestSafeAsync(
                query.ArtifactId,
                query.LeftVersion,
                cancellationToken)
            .ConfigureAwait(false);
        var rightManifest = await GetManifestSafeAsync(
                query.ArtifactId,
                query.RightVersion,
                cancellationToken)
            .ConfigureAwait(false);
        var leftReference = ToReference(query.ProjectId, leftManifest);
        var rightReference = ToReference(query.ProjectId, rightManifest);

        if (string.Equals(leftManifest.Sha256, rightManifest.Sha256, StringComparison.Ordinal))
        {
            return new ArtifactCompareResult(
                leftReference,
                rightReference,
                Identical: true,
                new ArtifactComparisonSummary(0, 0, 0, false, false),
                [],
                []);
        }

        var warnings = new List<string>();
        if (!IsTextCompatible(leftManifest.MediaType) ||
            !IsTextCompatible(rightManifest.MediaType))
        {
            warnings.Add("A line diff was not performed because one or both artifact versions are not text-compatible.");
            return MetadataOnly(leftReference, rightReference, warnings);
        }

        if (leftManifest.Length + rightManifest.Length > MaximumDiffBytes)
        {
            warnings.Add($"A line diff was not performed because combined content exceeds {MaximumDiffBytes} bytes.");
            return MetadataOnly(leftReference, rightReference, warnings);
        }

        var leftText = await ReadTextSafeAsync(
                query.ArtifactId,
                query.LeftVersion,
                MaximumDiffBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var rightText = await ReadTextSafeAsync(
                query.ArtifactId,
                query.RightVersion,
                MaximumDiffBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var leftLines = SplitLines(leftText);
        var rightLines = SplitLines(rightText);

        if (leftLines.Length > MaximumDiffLinesPerSide ||
            rightLines.Length > MaximumDiffLinesPerSide)
        {
            warnings.Add($"A line diff was not performed because one side exceeds {MaximumDiffLinesPerSide} lines.");
            return MetadataOnly(leftReference, rightReference, warnings);
        }

        var allDifferences = BuildLineDiff(leftLines, rightLines);
        var selected = allDifferences.Take(query.MaxDifferences).ToArray();
        var added = allDifferences.Count(item => item.Kind == "added");
        var removed = allDifferences.Count(item => item.Kind == "removed");
        var truncated = allDifferences.Count > selected.Length;
        if (truncated)
        {
            warnings.Add("The structured difference list was truncated to maxDifferences.");
        }

        return new ArtifactCompareResult(
            leftReference,
            rightReference,
            Identical: false,
            new ArtifactComparisonSummary(
                added,
                removed,
                allDifferences.Count,
                truncated,
                TextDiffPerformed: true),
            selected,
            warnings);
    }

    public async ValueTask<ArtifactResourceResult> ReadResourceAsync(
        ArtifactResourceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(query.ProjectId, query.ArtifactId);
        ValidateVersion(query.Version, nameof(query.Version));

        var manifest = await GetManifestSafeAsync(
                query.ArtifactId,
                query.Version,
                cancellationToken)
            .ConfigureAwait(false);
        if (manifest.Length > MaximumResourceBytes)
        {
            throw new ArtifactQueryException(
                "resource_too_large",
                $"Artifact content exceeds the {MaximumResourceBytes} byte resource limit.");
        }

        var bytes = await ReadBytesSafeAsync(
                query.ArtifactId,
                query.Version,
                MaximumResourceBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var reference = ToReference(query.ProjectId, manifest);
        if (IsTextCompatible(manifest.MediaType))
        {
            try
            {
                return new ArtifactResourceResult(
                    reference,
                    StrictUtf8.GetString(bytes),
                    BlobBase64: null);
            }
            catch (DecoderFallbackException)
            {
                throw new ArtifactQueryException(
                    "invalid_utf8",
                    "Artifact declares a text-compatible media type but is not valid UTF-8.");
            }
        }

        return new ArtifactResourceResult(
            reference,
            Text: null,
            Convert.ToBase64String(bytes));
    }

    private async ValueTask<ArtifactManifest> GetManifestSafeAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.GetManifestAsync(artifactId, version, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArtifactNotFoundException)
        {
            throw new ArtifactQueryException(
                "artifact_not_found",
                "The requested artifact version was not found.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new ArtifactQueryException(
                "artifact_integrity_failed",
                "Artifact integrity verification failed.");
        }
        catch (ArgumentException)
        {
            throw new ArtifactQueryException(
                "invalid_artifact",
                "Artifact identity or version is invalid.");
        }
    }

    private async ValueTask<string> ReadTextSafeAsync(
        string artifactId,
        int version,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesSafeAsync(
                artifactId,
                version,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ArtifactQueryException(
                "invalid_utf8",
                "Artifact text content is not valid UTF-8.");
        }
    }

    private async ValueTask<byte[]> ReadBytesSafeAsync(
        string artifactId,
        int version,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _store.OpenReadAsync(
                    artifactId,
                    version,
                    verifyIntegrity: true,
                    cancellationToken)
                .ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (memory.Length + read > maximumBytes)
                {
                    throw new ArtifactQueryException(
                        "content_too_large",
                        $"Artifact content exceeds the {maximumBytes} byte operation limit.");
                }
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return memory.ToArray();
        }
        catch (ArtifactQueryException)
        {
            throw;
        }
        catch (ArtifactNotFoundException)
        {
            throw new ArtifactQueryException(
                "artifact_not_found",
                "The requested artifact version was not found.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new ArtifactQueryException(
                "artifact_integrity_failed",
                "Artifact integrity verification failed.");
        }
    }

    private static ArtifactCompareResult MetadataOnly(
        ArtifactLogicalReference left,
        ArtifactLogicalReference right,
        IReadOnlyList<string> warnings)
    {
        return new ArtifactCompareResult(
            left,
            right,
            Identical: false,
            new ArtifactComparisonSummary(0, 0, 0, false, false),
            [],
            warnings);
    }

    private static ArtifactLogicalReference ToReference(
        string projectId,
        ArtifactManifest manifest)
    {
        return new ArtifactLogicalReference(
            manifest.ArtifactId,
            manifest.Version,
            manifest.Sha256,
            manifest.Length,
            manifest.MediaType,
            manifest.CreatedAtUtc,
            BuildResourceUri(projectId, manifest.ArtifactId, manifest.Version));
    }

    public static string BuildResourceUri(
        string projectId,
        string artifactId,
        int version) =>
        $"book://project/{projectId}/artifact/{artifactId}/versions/{version}";

    private static void ValidateScope(string projectId, string artifactId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !ProjectIdRegex().IsMatch(projectId))
        {
            throw new ArtifactQueryException(
                "invalid_project_id",
                "projectId must be a lowercase slug of at most 64 characters.");
        }
        if (string.IsNullOrWhiteSpace(artifactId) || !ArtifactIdRegex().IsMatch(artifactId))
        {
            throw new ArtifactQueryException(
                "invalid_artifact_id",
                "artifactId must be a lowercase slug of at most 128 characters.");
        }
        if (!artifactId.StartsWith(projectId + ".", StringComparison.Ordinal))
        {
            throw new ArtifactQueryException(
                "artifact_scope_violation",
                "Artifact does not belong to the requested project scope.");
        }
    }

    private static void ValidateVersion(int version, string field)
    {
        if (version < 1)
        {
            throw new ArtifactQueryException(
                "invalid_version",
                $"{field} must be a positive integer.");
        }
    }

    private static bool IsTextCompatible(string mediaType)
    {
        var normalized = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized.StartsWith("text/", StringComparison.Ordinal) ||
               normalized is "application/json" or
                   "application/ld+json" or
                   "application/xml" or
                   "application/xhtml+xml" or
                   "application/yaml" or
                   "application/x-yaml" or
                   "application/javascript" or
                   "application/markdown";
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static IReadOnlyList<ArtifactLineDifference> BuildLineDiff(
        string[] left,
        string[] right)
    {
        var lcs = new int[left.Length + 1, right.Length + 1];
        for (var leftIndex = left.Length - 1; leftIndex >= 0; leftIndex--)
        {
            for (var rightIndex = right.Length - 1; rightIndex >= 0; rightIndex--)
            {
                lcs[leftIndex, rightIndex] = string.Equals(
                        left[leftIndex],
                        right[rightIndex],
                        StringComparison.Ordinal)
                    ? lcs[leftIndex + 1, rightIndex + 1] + 1
                    : Math.Max(
                        lcs[leftIndex + 1, rightIndex],
                        lcs[leftIndex, rightIndex + 1]);
            }
        }

        var result = new List<ArtifactLineDifference>();
        var i = 0;
        var j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (string.Equals(left[i], right[j], StringComparison.Ordinal))
            {
                i++;
                j++;
                continue;
            }

            if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                result.Add(new ArtifactLineDifference(
                    "removed",
                    i + 1,
                    null,
                    BoundLine(left[i]),
                    null));
                i++;
            }
            else
            {
                result.Add(new ArtifactLineDifference(
                    "added",
                    null,
                    j + 1,
                    null,
                    BoundLine(right[j])));
                j++;
            }
        }

        while (i < left.Length)
        {
            result.Add(new ArtifactLineDifference(
                "removed",
                i + 1,
                null,
                BoundLine(left[i]),
                null));
            i++;
        }
        while (j < right.Length)
        {
            result.Add(new ArtifactLineDifference(
                "added",
                null,
                j + 1,
                null,
                BoundLine(right[j])));
            j++;
        }
        return result;
    }

    private static string BoundLine(string value) =>
        value.Length <= 512 ? value : value[..512];

    public static string BuildOperationId(params string[] components)
    {
        var canonical = string.Join('|', components);
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return "read-" + hash[..24];
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIdRegex();
}
