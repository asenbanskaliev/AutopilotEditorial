using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public interface IDeepBookCreationCheckpointStore
{
    ValueTask<DeepBookCreationCheckpoint?> GetAsync(string workspaceId, Guid journeyId, CancellationToken ct = default);
    ValueTask SaveAsync(DeepBookCreationCheckpoint checkpoint, CancellationToken ct = default);
}

public interface IDeepBookCreationPhaseExecutor
{
    ValueTask<DeepBookCreationPhaseResult> ExecuteAsync(DeepBookCreationPhaseRequest request, CancellationToken ct = default);
}

public sealed record DeepBookCreationArtifact(
    string Format,
    string MediaType,
    string FileName,
    long SizeBytes,
    string Sha256,
    string Provenance,
    bool PolicyApproved);

public sealed record DeepBookCreationPhaseRequest(
    Guid JourneyId,
    string WorkspaceId,
    JourneyPhase Phase,
    BookCreationBrief Brief,
    int Attempt,
    decimal CostSpent,
    string RequestDigest);

public sealed record DeepBookCreationPhaseResult(
    JourneyPhase Phase,
    bool Approved,
    bool Retryable,
    decimal Cost,
    string AuthorityType,
    Guid AuthorityId,
    long AuthorityRevision,
    string AuthorityDigest,
    IReadOnlyList<DeepBookCreationArtifact> Artifacts,
    string? FailureReason);

public sealed record DeepBookCreationCheckpoint(
    Guid JourneyId,
    string WorkspaceId,
    JourneyPhase NextPhase,
    int Attempt,
    decimal CostSpent,
    IReadOnlyList<JourneyAuthorityReference> Authorities,
    IReadOnlyList<DeepBookCreationArtifact> Artifacts,
    bool Completed,
    string CheckpointDigest,
    DateTimeOffset UpdatedAtUtc);

public sealed record DeepBookCreationProofResult(
    DeepBookCreationCheckpoint Checkpoint,
    bool Resumed,
    bool Completed,
    IReadOnlyList<DeepBookCreationArtifact> FinalArtifacts,
    string PackageDigest);

public sealed class DeepBookCreationProofException : Exception
{
    public DeepBookCreationProofException(string message) : base(message) { }
}

public sealed class DeepBookCreationProofOrchestrator
{
    private static readonly JourneyPhase[] OrderedPhases = Enum.GetValues<JourneyPhase>();
    private static readonly HashSet<string> RequiredFormats = new(StringComparer.OrdinalIgnoreCase)
    { "EPUB", "PDF", "DOCX", "KDP" };

    private readonly IDeepBookCreationCheckpointStore _checkpoints;
    private readonly IDeepBookCreationPhaseExecutor _executor;

    public DeepBookCreationProofOrchestrator(IDeepBookCreationCheckpointStore checkpoints, IDeepBookCreationPhaseExecutor executor)
    {
        _checkpoints = checkpoints;
        _executor = executor;
    }

    public async ValueTask<DeepBookCreationProofResult> RunAsync(BookCreationJourney journey, DateTimeOffset at, CancellationToken ct = default)
    {
        if (journey.Status is JourneyStatus.Cancelled or JourneyStatus.Failed)
            throw new DeepBookCreationProofException("Terminal failed or cancelled journey cannot execute.");
        if (!journey.Brief.OutputFormats.IsSupersetOf(RequiredFormats))
            throw new DeepBookCreationProofException("EPUB, PDF, DOCX and KDP outputs are required for deep proof.");

        var existing = await _checkpoints.GetAsync(journey.WorkspaceId, journey.JourneyId, ct);
        var resumed = existing is not null;
        var checkpoint = existing ?? NewCheckpoint(journey, at);
        if (checkpoint.Completed)
            return Final(checkpoint, resumed);

        var maxCost = journey.Brief.MaximumCost;
        while (!checkpoint.Completed)
        {
            var requestDigest = Digest($"{journey.JourneyId}|{checkpoint.NextPhase}|{checkpoint.Attempt}|{checkpoint.CostSpent}|{checkpoint.CheckpointDigest}");
            var result = await _executor.ExecuteAsync(new DeepBookCreationPhaseRequest(
                journey.JourneyId, journey.WorkspaceId, checkpoint.NextPhase, journey.Brief,
                checkpoint.Attempt, checkpoint.CostSpent, requestDigest), ct);

            if (result.Cost < 0) throw new DeepBookCreationProofException("Negative phase cost is invalid.");
            var nextCost = checkpoint.CostSpent + result.Cost;
            if (maxCost is not null && nextCost > maxCost.Value)
                throw new DeepBookCreationProofException("Cost ceiling exceeded; execution stopped before committing the phase.");

            if (!result.Approved)
            {
                if (!result.Retryable || checkpoint.Attempt >= journey.Autonomy.MaximumAutomaticRepairAttempts)
                    throw new DeepBookCreationProofException(result.FailureReason ?? "Phase failed and repair budget is exhausted.");

                checkpoint = checkpoint with
                {
                    Attempt = checkpoint.Attempt + 1,
                    CostSpent = nextCost,
                    CheckpointDigest = Digest($"retry|{requestDigest}|{result.FailureReason}|{checkpoint.Attempt + 1}"),
                    UpdatedAtUtc = at
                };
                await _checkpoints.SaveAsync(checkpoint, ct);
                continue;
            }

            ValidateArtifacts(result.Artifacts);
            var authority = new JourneyAuthorityReference(result.Phase, result.AuthorityType, result.AuthorityId,
                result.AuthorityRevision, result.AuthorityDigest, true, true);
            var authorities = checkpoint.Authorities.Concat(new[] { authority }).ToArray();
            var artifacts = checkpoint.Artifacts.Concat(result.Artifacts).ToArray();
            var index = Array.IndexOf(OrderedPhases, checkpoint.NextPhase);
            var complete = index == OrderedPhases.Length - 1;
            var nextPhase = complete ? JourneyPhase.ReleaseReady : OrderedPhases[index + 1];

            checkpoint = checkpoint with
            {
                NextPhase = nextPhase,
                Attempt = 0,
                CostSpent = nextCost,
                Authorities = authorities,
                Artifacts = artifacts,
                Completed = complete,
                CheckpointDigest = Digest($"commit|{requestDigest}|{result.AuthorityDigest}|{string.Join(';', result.Artifacts.Select(x => x.Sha256))}"),
                UpdatedAtUtc = at
            };
            await _checkpoints.SaveAsync(checkpoint, ct);
        }

        return Final(checkpoint, resumed);
    }

    private static DeepBookCreationCheckpoint NewCheckpoint(BookCreationJourney journey, DateTimeOffset at) =>
        new(journey.JourneyId, journey.WorkspaceId, JourneyPhase.Intake, 0, 0m,
            Array.Empty<JourneyAuthorityReference>(), Array.Empty<DeepBookCreationArtifact>(), false,
            Digest($"start|{journey.JourneyId}|{journey.WorkspaceId}|{journey.Brief.Idea}"), at);

    private static DeepBookCreationProofResult Final(DeepBookCreationCheckpoint checkpoint, bool resumed)
    {
        var finalArtifacts = checkpoint.Artifacts.Where(x => RequiredFormats.Contains(x.Format)).ToArray();
        var found = finalArtifacts.Select(x => x.Format).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!found.SetEquals(RequiredFormats))
            throw new DeepBookCreationProofException("Final package is incomplete.");
        if (finalArtifacts.Any(x => !x.PolicyApproved || x.SizeBytes <= 0 || string.IsNullOrWhiteSpace(x.Sha256)))
            throw new DeepBookCreationProofException("Final package contains an unapproved or unverifiable artifact.");
        return new(checkpoint, resumed, true, finalArtifacts,
            Digest(string.Join('|', finalArtifacts.OrderBy(x => x.Format).Select(x => $"{x.Format}:{x.Sha256}:{x.SizeBytes}"))));
    }

    private static void ValidateArtifacts(IEnumerable<DeepBookCreationArtifact> artifacts)
    {
        foreach (var artifact in artifacts)
        {
            if (artifact.SizeBytes <= 0 || string.IsNullOrWhiteSpace(artifact.FileName) ||
                string.IsNullOrWhiteSpace(artifact.Sha256) || string.IsNullOrWhiteSpace(artifact.Provenance))
                throw new DeepBookCreationProofException("Artifact evidence is incomplete.");
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class InMemoryDeepBookCreationCheckpointStore : IDeepBookCreationCheckpointStore
{
    private readonly Dictionary<(string WorkspaceId, Guid JourneyId), DeepBookCreationCheckpoint> _items = new();

    public ValueTask<DeepBookCreationCheckpoint?> GetAsync(string workspaceId, Guid journeyId, CancellationToken ct = default)
    {
        _items.TryGetValue((workspaceId, journeyId), out var checkpoint);
        return ValueTask.FromResult(checkpoint);
    }

    public ValueTask SaveAsync(DeepBookCreationCheckpoint checkpoint, CancellationToken ct = default)
    {
        _items[(checkpoint.WorkspaceId, checkpoint.JourneyId)] = checkpoint;
        return ValueTask.CompletedTask;
    }
}
