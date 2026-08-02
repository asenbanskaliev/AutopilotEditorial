namespace BookStudio.Application.Authoring;

public sealed record ProviderBackedDeepBookProofResult(
    DeepBookProofCheckpoint Checkpoint,
    PublicationArtifactResult? Publication,
    bool ReadyForPublication);

public sealed class ProviderBackedDeepBookProofAuthority
{
    private readonly DeepBookProofCoordinator _coordinator;
    private readonly PublicationArtifactPipeline _pipeline;
    private readonly string _providerId;
    private readonly string _workspaceRoot;

    public ProviderBackedDeepBookProofAuthority(
        DeepBookProofCoordinator coordinator,
        PublicationArtifactPipeline pipeline,
        string providerId,
        string workspaceRoot)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _providerId = string.IsNullOrWhiteSpace(providerId)
            ? throw new ArgumentException("Provider id is required.", nameof(providerId))
            : providerId;
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public async ValueTask<ProviderBackedDeepBookProofResult> ExecuteAsync(
        DeepBookProofRequest request,
        BookCreationJourney journey,
        string title,
        string author,
        string language,
        string manuscript,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        PublicationArtifactResult? publication = null;

        for (var step = 0; step < 8; step++)
        {
            ct.ThrowIfCancellationRequested();
            var current = await _coordinator.StartOrResumeAsync(request, journey, 0m, null, at, ct);
            if (current.Checkpoint.Status is DeepBookProofStatus.Ready or DeepBookProofStatus.Cancelled or DeepBookProofStatus.Failed or DeepBookProofStatus.WaitingForDecision)
                return new ProviderBackedDeepBookProofResult(current.Checkpoint, publication, current.ReadyForPublication);

            if (current.Checkpoint.Phase != DeepBookProofPhase.ArtifactProduction)
                continue;

            var preExisting = Directory.Exists(_workspaceRoot)
                ? Directory.EnumerateFiles(_workspaceRoot, "*", SearchOption.AllDirectories)
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var artifactRequest = new PublicationArtifactRequest(
                request.ProofId,
                request.WorkspaceId,
                title,
                author,
                language,
                manuscript,
                request.Policy.RequiredFormats,
                Math.Max(0m, request.Policy.MaximumCost - current.Checkpoint.AccumulatedCost),
                request.Policy.CostCurrency);

            var produced = await _pipeline.ProduceAsync(artifactRequest, _providerId, _workspaceRoot, ct);
            var reusedBeforeExecution = produced.Artifacts.All(artifact =>
                preExisting.Contains(Path.GetFullPath(Path.Combine(
                    _workspaceRoot,
                    artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)))));
            publication = produced with { ReusedExistingArtifacts = reusedBeforeExecution };

            var accepted = await _coordinator.StartOrResumeAsync(
                request,
                journey,
                publication.Cost,
                publication.Artifacts,
                at,
                ct);
            if (accepted.Checkpoint.Status is DeepBookProofStatus.WaitingForDecision or DeepBookProofStatus.Failed)
                return new ProviderBackedDeepBookProofResult(accepted.Checkpoint, publication, false);
        }

        var final = await _coordinator.StartOrResumeAsync(request, journey, 0m, null, at, ct);
        if (!final.ReadyForPublication)
            throw new DeepBookProofValidationException("Provider-backed proof did not reach publication readiness within the bounded autonomous cycle.");
        return new ProviderBackedDeepBookProofResult(final.Checkpoint, publication, true);
    }
}
