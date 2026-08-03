using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed record PublicationArtifact(string Name, string MediaType, byte[] Content);
public sealed record PublicationManifestEntry(string Name, string MediaType, long Length, string Sha256);
public sealed record PublicationPackageResult(string ZipPath, string ManifestPath, IReadOnlyList<PublicationManifestEntry> Entries);

public sealed class FinalPublicationJourney
{
    private static readonly string[] Required = ["manuscript.epub", "interior.pdf", "cover.pdf", "metadata.json", "publication-checklist.md"];

    public async ValueTask<PublicationPackageResult> BuildAsync(
        string projectId,
        IReadOnlyList<PublicationArtifact> artifacts,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (artifacts is null || artifacts.Count == 0) throw new InvalidDataException("Publication artifacts are required.");

        var duplicates = artifacts.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        if (duplicates.Length > 0) throw new InvalidDataException($"Duplicate publication artifacts: {string.Join(", ", duplicates)}");
        var missing = Required.Where(required => artifacts.All(x => !string.Equals(x.Name, required, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"Missing required publication artifacts: {string.Join(", ", missing)}");
        if (artifacts.Any(x => x.Content is null || x.Content.Length == 0)) throw new InvalidDataException("Empty publication artifacts are forbidden.");

        Directory.CreateDirectory(outputDirectory);
        var entries = artifacts.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new PublicationManifestEntry(
            x.Name,
            x.MediaType,
            x.Content.LongLength,
            Convert.ToHexString(SHA256.HashData(x.Content)).ToLowerInvariant())).ToArray();

        var manifestPath = Path.Combine(outputDirectory, $"{Safe(projectId)}-publication-manifest.json");
        var manifestJson = JsonSerializer.Serialize(new { projectId, generatedAtUtc = DateTimeOffset.UtcNow, entries }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        var zipPath = Path.Combine(outputDirectory, $"{Safe(projectId)}-kdp-publication-package.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var artifact in artifacts.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(artifact.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = DateTimeOffset.UnixEpoch;
                await using var stream = entry.Open();
                await stream.WriteAsync(artifact.Content, cancellationToken).ConfigureAwait(false);
            }
            var manifestEntry = archive.CreateEntry("publication-manifest.json", CompressionLevel.Optimal);
            manifestEntry.LastWriteTime = DateTimeOffset.UnixEpoch;
            await using var manifestStream = manifestEntry.Open();
            var bytes = Encoding.UTF8.GetBytes(manifestJson);
            await manifestStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        return new PublicationPackageResult(zipPath, manifestPath, entries);
    }

    public static void Verify(PublicationPackageResult result)
    {
        if (!File.Exists(result.ZipPath) || !File.Exists(result.ManifestPath)) throw new InvalidDataException("Publication package is incomplete.");
        using var archive = ZipFile.OpenRead(result.ZipPath);
        foreach (var expected in result.Entries)
        {
            var entry = archive.GetEntry(expected.Name) ?? throw new InvalidDataException($"Missing ZIP entry {expected.Name}.");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var hash = Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant();
            if (!string.Equals(hash, expected.Sha256, StringComparison.Ordinal)) throw new InvalidDataException($"Hash mismatch for {expected.Name}.");
        }
    }

    private static string Safe(string value) => string.Concat(value.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
}
