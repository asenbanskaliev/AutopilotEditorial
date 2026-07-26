using System.Text.RegularExpressions;
using BookStudio.Application.Artifacts;

namespace BookStudio.Infrastructure.Artifacts.FileSystem;

internal static partial class ArtifactPathPolicy
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIdRegex();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    public static string ValidateArtifactId(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        if (!ArtifactIdRegex().IsMatch(artifactId))
        {
            throw new ArgumentException("Artifact ID must be a lowercase slug of at most 128 characters.", nameof(artifactId));
        }
        return artifactId;
    }

    public static string ValidateMediaType(string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (mediaType.Length > 255 || !mediaType.Contains('/', StringComparison.Ordinal) || mediaType.Any(char.IsControl))
        {
            throw new ArgumentException("Media type is invalid.", nameof(mediaType));
        }
        return mediaType;
    }

    public static string ValidateSha256(string sha256)
    {
        if (!Sha256Regex().IsMatch(sha256))
        {
            throw new ArtifactIntegrityException("Manifest SHA-256 is malformed.");
        }
        return sha256;
    }

    public static string Confine(string root, params string[] segments)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine([canonicalRoot, .. segments]));
        var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison) && !string.Equals(candidate, canonicalRoot, PathComparison))
        {
            throw new ArtifactStoreException("Artifact path escaped the configured store root.");
        }
        return candidate;
    }

    public static void EnsureNoLinks(string root, string candidate)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var canonicalCandidate = Path.GetFullPath(candidate);
        _ = Confine(canonicalRoot, Path.GetRelativePath(canonicalRoot, canonicalCandidate));

        var current = canonicalRoot;
        RejectLinkIfExists(current);
        var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);
        if (relative == ".")
        {
            return;
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectLinkIfExists(current);
        }
    }

    public static void CreateDirectorySecure(string root, string directory)
    {
        _ = Confine(root, Path.GetRelativePath(root, directory));
        EnsureNoLinks(root, directory);
        Directory.CreateDirectory(directory);
        EnsureNoLinks(root, directory);
    }

    private static void RejectLinkIfExists(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArtifactStoreException($"Symbolic links and reparse points are not allowed in artifact paths: {path}");
        }

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new ArtifactStoreException($"Symbolic links are not allowed in artifact paths: {path}");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
