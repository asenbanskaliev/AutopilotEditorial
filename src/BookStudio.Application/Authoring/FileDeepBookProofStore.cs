using System.Text.Json;

namespace BookStudio.Application.Authoring;

public sealed class FileDeepBookProofStore : IDeepBookProofStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileDeepBookProofStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Store root is required.", nameof(root));
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<DeepBookProofCheckpoint?> LoadAsync(string workspaceId, Guid proofId, CancellationToken ct = default)
    {
        ValidateIdentity(workspaceId, proofId);
        var path = PathFor(workspaceId, proofId);
        if (!File.Exists(path)) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var checkpoint = await JsonSerializer.DeserializeAsync<DeepBookProofCheckpoint>(stream, JsonOptions, ct)
            ?? throw new DeepBookProofValidationException("Checkpoint payload is empty.");
        if (!StringComparer.Ordinal.Equals(checkpoint.WorkspaceId, workspaceId) || checkpoint.ProofId != proofId)
            throw new DeepBookProofValidationException("Checkpoint identity does not match the requested workspace.");
        return checkpoint;
    }

    public async ValueTask SaveAsync(DeepBookProofCheckpoint checkpoint, long expectedRevision, CancellationToken ct = default)
    {
        ValidateIdentity(checkpoint.WorkspaceId, checkpoint.ProofId);
        await _gate.WaitAsync(ct);
        try
        {
            var path = PathFor(checkpoint.WorkspaceId, checkpoint.ProofId);
            var existing = await LoadWithoutGateAsync(path, ct);
            var actual = existing?.Revision ?? 0;
            if (actual != expectedRevision)
                throw new DeepBookProofConflictException($"Expected revision {expectedRevision} but found {actual}.");
            if (checkpoint.Revision != expectedRevision + 1)
                throw new DeepBookProofConflictException("Checkpoint revision must advance exactly once.");

            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<DeepBookProofCheckpoint?> LoadWithoutGateAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<DeepBookProofCheckpoint>(stream, JsonOptions, ct);
    }

    private string PathFor(string workspaceId, Guid proofId)
    {
        var safeWorkspace = string.Concat(workspaceId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var workspaceRoot = Path.GetFullPath(Path.Combine(_root, safeWorkspace));
        if (!workspaceRoot.StartsWith(_root, StringComparison.Ordinal))
            throw new DeepBookProofValidationException("Workspace path escapes the store root.");
        return Path.Combine(workspaceRoot, proofId.ToString("N") + ".json");
    }

    private static void ValidateIdentity(string workspaceId, Guid proofId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || proofId == Guid.Empty)
            throw new DeepBookProofValidationException("Workspace and proof identity are required.");
    }
}
