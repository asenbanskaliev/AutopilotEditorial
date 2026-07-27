using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BookStudio.Application.Artifacts;

namespace BookStudio.Application.Authoring;

/// <summary>Registers immutable draft versions and validates stored textual drafts.</summary>
public sealed partial class DraftAuthoringService : IDraftAuthoringService
{
    public const int MaximumRegistrationBytes = 512 * 1024;
    public const int MaximumResourceBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IArtifactStore _store;

    public DraftAuthoringService(IArtifactStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<DraftRegistrationResult> RegisterAsync(
        DraftRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateProjectAndArtifact(command.ProjectId, command.ArtifactId);
        if (command.ExpectedVersion < 1)
        {
            throw new DraftAuthoringException("invalid_version", "Expected version must be positive.");
        }

        var mediaType = NormalizeMediaType(command.MediaType);
        if (string.IsNullOrEmpty(command.Content))
        {
            throw new DraftAuthoringException("empty_draft", "Draft content cannot be empty.");
        }
        if (ContainsForbiddenControl(command.Content))
        {
            throw new DraftAuthoringException(
                "invalid_draft_controls",
                "Draft content contains a forbidden control character.");
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(command.Content);
        }
        catch (EncoderFallbackException)
        {
            throw new DraftAuthoringException("invalid_utf8", "Draft content is not valid Unicode text.");
        }

        if (bytes.Length > MaximumRegistrationBytes)
        {
            throw new DraftAuthoringException(
                "draft_too_large",
                $"Draft content exceeds {MaximumRegistrationBytes.ToString(CultureInfo.InvariantCulture)} UTF-8 bytes.");
        }

        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            var manifest = await _store.PutAsync(
                    new ArtifactWriteRequest(
                        command.ArtifactId,
                        command.ExpectedVersion,
                        mediaType,
                        stream),
                    cancellationToken)
                .ConfigureAwait(false);
            return new DraftRegistrationResult(ToReference(command.ProjectId, manifest), []);
        }
        catch (ArtifactVersionConflictException exception)
        {
            throw new DraftAuthoringException(
                "draft_version_conflict",
                $"Draft version conflict. Required version is {exception.RequiredVersion.ToString(CultureInfo.InvariantCulture)}.");
        }
        catch (ArtifactSizeLimitExceededException)
        {
            throw new DraftAuthoringException("draft_too_large", "Draft content exceeds the configured artifact limit.");
        }
        catch (ArtifactStoreQuotaExceededException)
        {
            throw new DraftAuthoringException(
                "artifact_store_quota_exceeded",
                "Artifact Store quota prevents publishing this draft version.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new DraftAuthoringException("artifact_integrity_failed", "Draft storage integrity verification failed.");
        }
        catch (ArgumentException)
        {
            throw new DraftAuthoringException("invalid_draft", "Draft registration parameters are invalid.");
        }
    }

    public async ValueTask<DraftValidationResult> ValidateAsync(
        DraftValidationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateProjectAndArtifact(query.ProjectId, query.ArtifactId);
        if (query.Version < 1)
        {
            throw new DraftAuthoringException("invalid_version", "Draft version must be positive.");
        }
        if (query.MaximumLineLength is < 40 or > 240)
        {
            throw new DraftAuthoringException(
                "invalid_line_limit",
                "Maximum line length must be between 40 and 240 characters.");
        }

        var (manifest, text) = await ReadTextAsync(
                query.ProjectId,
                query.ArtifactId,
                query.Version,
                cancellationToken)
            .ConfigureAwait(false);
        var lines = SplitLines(text);
        var warnings = BuildWarnings(text, lines, query.MaximumLineLength);
        var metrics = new DraftValidationMetrics(
            text.Length,
            CountWords(text),
            lines.Length,
            CountParagraphs(lines),
            lines.Count(line => line.TrimStart().StartsWith('#')));
        return new DraftValidationResult(
            ToReference(query.ProjectId, manifest),
            metrics,
            warnings,
            !warnings.Any(warning => warning.Code is "empty_content" or "nul_character" or "control_character"));
    }

    public async ValueTask<DraftResourceResult> ReadResourceAsync(
        DraftResourceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateProjectAndArtifact(query.ProjectId, query.ArtifactId);
        if (query.Version < 1)
        {
            throw new DraftAuthoringException("invalid_version", "Draft version must be positive.");
        }

        var (manifest, text) = await ReadTextAsync(
                query.ProjectId,
                query.ArtifactId,
                query.Version,
                cancellationToken)
            .ConfigureAwait(false);
        return new DraftResourceResult(ToReference(query.ProjectId, manifest), text);
    }

    public static string BuildOperationId(params string[] parts)
    {
        var payload = string.Join('|', parts);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private async ValueTask<(ArtifactManifest Manifest, string Text)> ReadTextAsync(
        string projectId,
        string artifactId,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _store.GetManifestAsync(artifactId, version, cancellationToken)
                .ConfigureAwait(false);
            _ = NormalizeMediaType(manifest.MediaType);
            if (manifest.Length > MaximumResourceBytes)
            {
                throw new DraftAuthoringException(
                    "draft_too_large_to_validate",
                    "Draft is too large for bounded validation or resource reading.");
            }

            await using var stream = await _store.OpenReadAsync(
                    artifactId,
                    version,
                    verifyIntegrity: true,
                    cancellationToken)
                .ConfigureAwait(false);
            using var memory = new MemoryStream((int)manifest.Length);
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            if (memory.Length != manifest.Length || memory.Length > MaximumResourceBytes)
            {
                throw new DraftAuthoringException("artifact_integrity_failed", "Draft length verification failed.");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
            }
            catch (DecoderFallbackException)
            {
                throw new DraftAuthoringException("invalid_utf8", "Stored draft is not valid UTF-8 text.");
            }
            return (manifest, text);
        }
        catch (DraftAuthoringException)
        {
            throw;
        }
        catch (ArtifactNotFoundException)
        {
            throw new DraftAuthoringException("draft_not_found", "The requested draft version was not found.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new DraftAuthoringException("artifact_integrity_failed", "Draft integrity verification failed.");
        }
        catch (ArgumentException)
        {
            throw new DraftAuthoringException("invalid_draft", "Draft reference is invalid.");
        }
    }

    private static IReadOnlyList<DraftWarning> BuildWarnings(
        string text,
        IReadOnlyList<string> lines,
        int maximumLineLength)
    {
        var warnings = new List<DraftWarning>();
        AddWarning(warnings, "empty_content", "Draft content is empty or whitespace-only.", string.IsNullOrWhiteSpace(text) ? 1 : 0);
        AddWarning(warnings, "line_too_long", "One or more lines exceed the configured line length.", lines.Count(line => line.Length > maximumLineLength));
        AddWarning(warnings, "trailing_whitespace", "One or more lines contain trailing whitespace.", lines.Count(line => line.Length > 0 && char.IsWhiteSpace(line[^1])));
        AddWarning(warnings, "tab_character", "Draft contains tab characters.", text.Count(character => character == '\t'));
        AddWarning(warnings, "nul_character", "Draft contains NUL characters.", text.Count(character => character == '\0'));
        AddWarning(
            warnings,
            "control_character",
            "Draft contains unsupported control characters.",
            text.Count(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\0'));
        return warnings;
    }

    private static void AddWarning(
        ICollection<DraftWarning> warnings,
        string code,
        string message,
        int count)
    {
        if (count > 0)
        {
            warnings.Add(new DraftWarning(code, message, count));
        }
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static int CountParagraphs(IReadOnlyList<string> lines)
    {
        var paragraphs = 0;
        var insideParagraph = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                insideParagraph = false;
            }
            else if (!insideParagraph)
            {
                paragraphs++;
                insideParagraph = true;
            }
        }
        return paragraphs;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static bool ContainsForbiddenControl(string text) =>
        text.Any(character =>
            char.IsControl(character) && character is not '\r' and not '\n' and not '\t');

    private static string NormalizeMediaType(string mediaType)
    {
        if (string.Equals(mediaType, "text/markdown", StringComparison.OrdinalIgnoreCase))
        {
            return "text/markdown";
        }
        if (string.Equals(mediaType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain";
        }
        throw new DraftAuthoringException(
            "unsupported_media_type",
            "Draft media type must be text/markdown or text/plain.");
    }

    private static void ValidateProjectAndArtifact(string projectId, string artifactId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !ProjectIdRegex().IsMatch(projectId))
        {
            throw new DraftAuthoringException("invalid_project_id", "Project ID is invalid.");
        }
        if (string.IsNullOrWhiteSpace(artifactId) || !ArtifactIdRegex().IsMatch(artifactId))
        {
            throw new DraftAuthoringException("invalid_artifact_id", "Draft artifact ID is invalid.");
        }
        if (!artifactId.StartsWith(projectId + ".draft.", StringComparison.Ordinal))
        {
            throw new DraftAuthoringException(
                "draft_scope_violation",
                "Draft artifact does not belong to the requested project scope.");
        }
    }

    private static DraftArtifactReference ToReference(
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

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIdRegex();
}
