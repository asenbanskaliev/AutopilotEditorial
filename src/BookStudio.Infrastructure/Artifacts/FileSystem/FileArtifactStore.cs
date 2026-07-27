using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using BookStudio.Application.Artifacts;

namespace BookStudio.Infrastructure.Artifacts.FileSystem;

/// <summary>Immutable content-addressed artifact storage rooted inside one workspace.</summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private const string ManifestSchemaVersion = "1.0.0";
    private const long ManifestQuotaReserveBytes = 64L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly FileArtifactStoreOptions _options;
    private readonly string _blobsRoot;
    private readonly string _manifestsRoot;
    private readonly string _tempRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artifactLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public FileArtifactStore(FileArtifactStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory.CreateDirectory(_options.WorkspaceRoot);
        ArtifactPathPolicy.EnsureNoLinks(_options.WorkspaceRoot, _options.WorkspaceRoot);
        ArtifactPathPolicy.CreateDirectorySecure(_options.WorkspaceRoot, _options.StoreRoot);

        _blobsRoot = ArtifactPathPolicy.Confine(_options.StoreRoot, "blobs", "sha256");
        _manifestsRoot = ArtifactPathPolicy.Confine(_options.StoreRoot, "manifests");
        _tempRoot = ArtifactPathPolicy.Confine(_options.StoreRoot, "temp");

        ArtifactPathPolicy.CreateDirectorySecure(_options.StoreRoot, _blobsRoot);
        ArtifactPathPolicy.CreateDirectorySecure(_options.StoreRoot, _manifestsRoot);
        ArtifactPathPolicy.CreateDirectorySecure(_options.StoreRoot, _tempRoot);
    }

    public async ValueTask<ArtifactManifest> PutAsync(
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(request);
        var artifactId = ArtifactPathPolicy.ValidateArtifactId(request.ArtifactId);
        var mediaType = ArtifactPathPolicy.ValidateMediaType(request.MediaType);
        if (request.ExpectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Expected version must be positive.");
        }
        if (request.Content is null || !request.Content.CanRead)
        {
            throw new ArgumentException("Artifact content must be a readable stream.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, _tempRoot);
        var contentTempPath = ArtifactPathPolicy.Confine(_tempRoot, $"{Guid.NewGuid():N}.tmp");

        try
        {
            var (sha256, length) = await WriteAndHashTempAsync(
                    request.Content,
                    contentTempPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var artifactLock = _artifactLocks.GetOrAdd(
                artifactId,
                static _ => new SemaphoreSlim(1, 1));
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await artifactLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var manifestDirectory = ArtifactPathPolicy.Confine(_manifestsRoot, artifactId);
                    ArtifactPathPolicy.CreateDirectorySecure(_options.StoreRoot, manifestDirectory);
                    var requiredVersion = GetRequiredVersion(manifestDirectory);
                    if (request.ExpectedVersion != requiredVersion)
                    {
                        throw new ArtifactVersionConflictException(
                            artifactId,
                            request.ExpectedVersion,
                            requiredVersion);
                    }

                    EnsureWriteQuota();
                    var blobPath = await PromoteBlobAsync(
                            contentTempPath,
                            sha256,
                            length,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, blobPath);

                    var manifest = new ArtifactManifest(
                        ManifestSchemaVersion,
                        artifactId,
                        request.ExpectedVersion,
                        sha256,
                        length,
                        mediaType,
                        DateTimeOffset.UtcNow);
                    await PublishManifestAsync(
                            manifestDirectory,
                            manifest,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return manifest;
                }
                finally
                {
                    artifactLock.Release();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            DeleteIfExists(contentTempPath);
        }
    }

    public async ValueTask<ArtifactManifest> GetManifestAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        artifactId = ArtifactPathPolicy.ValidateArtifactId(artifactId);
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var manifestPath = GetManifestPath(artifactId, version);
        ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new ArtifactNotFoundException(artifactId, version);
        }

        ArtifactManifest? manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<ArtifactManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new ArtifactIntegrityException("Artifact manifest JSON is malformed.", exception);
        }

        ValidateManifest(manifest, artifactId, version);
        return manifest!;
    }

    public async ValueTask<Stream> OpenReadAsync(
        string artifactId,
        int version,
        bool verifyIntegrity = true,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        var manifest = await GetManifestAsync(artifactId, version, cancellationToken)
            .ConfigureAwait(false);
        var blobPath = GetBlobPath(manifest.Sha256);
        ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, blobPath);
        if (!File.Exists(blobPath))
        {
            throw new ArtifactIntegrityException("Artifact blob is missing.");
        }

        if (verifyIntegrity)
        {
            await VerifyBlobAsync(blobPath, manifest, cancellationToken)
                .ConfigureAwait(false);
        }

        return new FileStream(
            blobPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            _options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async ValueTask<IReadOnlyList<ArtifactManifest>> ListVersionsAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        artifactId = ArtifactPathPolicy.ValidateArtifactId(artifactId);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = ArtifactPathPolicy.Confine(_manifestsRoot, artifactId);
        ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, directory);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<ArtifactManifest>();
        }

        var versions = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(value => int.TryParse(value, out var parsed) ? parsed : -1)
            .Where(item => item > 0)
            .Order()
            .ToArray();
        var manifests = new List<ArtifactManifest>(versions.Length);
        foreach (var item in versions)
        {
            manifests.Add(await GetManifestAsync(artifactId, item, cancellationToken)
                .ConfigureAwait(false));
        }
        return manifests;
    }

    public async ValueTask VerifyAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenReadAsync(
                artifactId,
                version,
                verifyIntegrity: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _writeGate.Dispose();
            foreach (var artifactLock in _artifactLocks.Values)
            {
                artifactLock.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }

    private async Task<(string Sha256, long Length)> WriteAndHashTempAsync(
        Stream source,
        string tempPath,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.BufferSize];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using var destination = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            _options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (true)
        {
            var read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length = checked(length + read);
            if (length > _options.MaximumArtifactBytes)
            {
                throw new ArtifactSizeLimitExceededException(_options.MaximumArtifactBytes);
            }
            hasher.AppendData(buffer, 0, read);
            await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        var sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return (sha256, length);
    }

    private void EnsureWriteQuota()
    {
        var usage = MeasureStore();
        long observedBytes;
        long observedFiles;
        try
        {
            observedBytes = checked(usage.Bytes + ManifestQuotaReserveBytes);
            observedFiles = checked(usage.Files + 1L);
        }
        catch (OverflowException)
        {
            throw new ArtifactStoreQuotaExceededException(
                "bytes",
                _options.MaximumStoreBytes,
                long.MaxValue);
        }

        if (observedBytes > _options.MaximumStoreBytes)
        {
            throw new ArtifactStoreQuotaExceededException(
                "bytes",
                _options.MaximumStoreBytes,
                observedBytes);
        }
        if (observedFiles > _options.MaximumStoreFiles)
        {
            throw new ArtifactStoreQuotaExceededException(
                "files",
                _options.MaximumStoreFiles,
                observedFiles);
        }
    }

    private StoreUsage MeasureStore()
    {
        ArtifactPathPolicy.EnsureNoLinks(_options.WorkspaceRoot, _options.StoreRoot);
        var pending = new Stack<string>();
        pending.Push(_options.StoreRoot);
        long bytes = 0;
        long files = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArtifactStoreException("Links and reparse points are not allowed in the Artifact Store.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                files = checked(files + 1L);
                bytes = checked(bytes + new FileInfo(entry).Length);
            }
        }

        return new StoreUsage(bytes, files);
    }

    private async Task<string> PromoteBlobAsync(
        string tempPath,
        string sha256,
        long length,
        CancellationToken cancellationToken)
    {
        var blobPath = GetBlobPath(sha256);
        var blobDirectory = Path.GetDirectoryName(blobPath)
            ?? throw new ArtifactStoreException("Blob path has no directory.");
        ArtifactPathPolicy.CreateDirectorySecure(_options.StoreRoot, blobDirectory);

        try
        {
            File.Move(tempPath, blobPath);
        }
        catch (IOException) when (File.Exists(blobPath))
        {
            await VerifyBlobAsync(
                    blobPath,
                    new ArtifactManifest(
                        ManifestSchemaVersion,
                        "deduplicated-blob",
                        1,
                        sha256,
                        length,
                        "application/octet-stream",
                        DateTimeOffset.UnixEpoch),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, blobPath);
        return blobPath;
    }

    private async Task PublishManifestAsync(
        string manifestDirectory,
        ArtifactManifest manifest,
        CancellationToken cancellationToken)
    {
        var targetPath = GetManifestPath(manifest.ArtifactId, manifest.Version);
        var tempPath = ArtifactPathPolicy.Confine(
            manifestDirectory,
            $".{manifest.Version}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             _options.BufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, targetPath);
            }
            catch (IOException exception) when (File.Exists(targetPath))
            {
                throw new ArtifactVersionConflictException(
                    manifest.ArtifactId,
                    manifest.Version,
                    GetRequiredVersion(manifestDirectory))
                {
                    Source = exception.Source,
                };
            }
        }
        finally
        {
            DeleteIfExists(tempPath);
        }
    }

    private async Task VerifyBlobAsync(
        string blobPath,
        ArtifactManifest manifest,
        CancellationToken cancellationToken)
    {
        ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, blobPath);
        var buffer = new byte[_options.BufferSize];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using var stream = new FileStream(
            blobPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            _options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            length = checked(length + read);
            hasher.AppendData(buffer, 0, read);
        }

        var actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (length != manifest.Length ||
            !string.Equals(actualHash, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new ArtifactIntegrityException(
                $"Artifact blob integrity mismatch. Expected {manifest.Sha256}/{manifest.Length}, actual {actualHash}/{length}.");
        }
    }

    private void ValidateManifest(
        ArtifactManifest? manifest,
        string artifactId,
        int version)
    {
        if (manifest is null ||
            !string.Equals(manifest.SchemaVersion, ManifestSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.ArtifactId, artifactId, StringComparison.Ordinal) ||
            manifest.Version != version ||
            manifest.Length < 0 ||
            manifest.CreatedAtUtc == default)
        {
            throw new ArtifactIntegrityException("Artifact manifest identity or required fields are invalid.");
        }
        _ = ArtifactPathPolicy.ValidateArtifactId(manifest.ArtifactId);
        _ = ArtifactPathPolicy.ValidateSha256(manifest.Sha256);
        _ = ArtifactPathPolicy.ValidateMediaType(manifest.MediaType);
    }

    private int GetRequiredVersion(string manifestDirectory)
    {
        if (!Directory.Exists(manifestDirectory))
        {
            return 1;
        }
        ArtifactPathPolicy.EnsureNoLinks(_options.StoreRoot, manifestDirectory);
        var maximum = Directory.EnumerateFiles(
                manifestDirectory,
                "*.json",
                SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(value => int.TryParse(value, out var version) ? version : 0)
            .DefaultIfEmpty(0)
            .Max();
        return checked(maximum + 1);
    }

    private string GetBlobPath(string sha256)
    {
        sha256 = ArtifactPathPolicy.ValidateSha256(sha256);
        return ArtifactPathPolicy.Confine(_blobsRoot, sha256[..2], sha256[2..]);
    }

    private string GetManifestPath(string artifactId, int version) =>
        ArtifactPathPolicy.Confine(_manifestsRoot, artifactId, $"{version}.json");

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record StoreUsage(long Bytes, long Files);
}
