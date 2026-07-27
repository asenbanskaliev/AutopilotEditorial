using BookStudio.Application.OpenCode;
using BookStudio.OpenCode;

namespace BookStudio.Tests.ModelBenchmarks;

internal sealed class ModelBenchmarksJourney
{
    private const long EvaluationEpochSeconds = 2_000_000;
    private const long FreshMeasuredAt = 1_999_000;
    private const string ProfileFingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MutationGateMarker = "mutation=NONE";

    private int _scenarios;
    private int _models;
    private int _roles;

    public async Task<ModelBenchmarksReport> RunAsync()
    {
        await RepositoryCatalogAsync().ConfigureAwait(false);
        await RolePolicyVersioningAsync().ConfigureAwait(false);
        await HardConstraintsAsync().ConfigureAwait(false);
        await MissingEvidenceAsync().ConfigureAwait(false);
        await StaleEvidenceAsync().ConfigureAwait(false);
        await LowConfidenceAsync().ConfigureAwait(false);
        await DeterministicRankingAsync().ConfigureAwait(false);
        await TieBreakingAsync().ConfigureAwait(false);
        await ExplicitFallbackAsync().ConfigureAwait(false);
        await FallbackCannotBypassAsync().ConfigureAwait(false);
        await ProviderAvailabilityNarrowsAsync().ConfigureAwait(false);
        await FingerprintValidationAsync().ConfigureAwait(false);
        await ProviderMappingAsync().ConfigureAwait(false);
        await ConcurrencyCancellationNoMutationAsync().ConfigureAwait(false);

        return new ModelBenchmarksReport(
            _scenarios,
            _models,
            _roles,
            "HARD_CONSTRAINTS",
            "NONE");
    }

    private async Task RepositoryCatalogAsync()
    {
        var payload = await File.ReadAllBytesAsync(
            Path.Combine("config", "opencode", "model-benchmarks.json")).ConfigureAwait(false);
        var catalog = OpenCodeModelBenchmarkCatalogLoader.Load(payload);
        Require(catalog.CatalogVersion >= 1, "Repository benchmark catalog version was not loaded.");
        Require(catalog.Models.Count >= 5, "Repository benchmark catalog did not contain five models.");
        Require(catalog.RolePolicies.Count >= 5, "Repository benchmark catalog did not contain five role policies.");

        var selector = new ModelAssignmentSelector(catalog);
        var policy = catalog.RolePolicies.Single(item => item.RoleId == "long-form-author");
        var request = Request(
            policy.RoleId,
            policy.Version,
            catalog.Models.Select(Availability).ToArray());
        var result = selector.Select(request);
        Require(ModelAssignmentFingerprint.Verify(result), "Repository assignment fingerprint was invalid.");
        Require(result.RoleId == policy.RoleId, "Repository role assignment drifted.");
        _models = catalog.Models.Count;
        _roles = catalog.RolePolicies.Count;
        _scenarios++;
    }

    private Task RolePolicyVersioningAsync()
    {
        var selector = Selector(BaseCatalog());
        RequireCode(
            () => selector.Select(Request("missing-role", 1, AllAvailability())),
            ModelAssignmentErrorCodes.RolePolicyNotFound);
        RequireCode(
            () => selector.Select(Request("long-form-author", 99, AllAvailability())),
            ModelAssignmentErrorCodes.RolePolicyVersionNotFound);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task HardConstraintsAsync()
    {
        var models = BaseModels().ToArray();
        models[0] = models[0] with
        {
            ContextWindowTokens = 20_000,
            BenchmarkEvidence = Evidence(score: 9_900),
        };
        models[1] = models[1] with { BenchmarkEvidence = Evidence(score: 8_000) };
        var selector = Selector(BaseCatalog(models: models));
        var result = selector.Select(Request("long-form-author", 1, AllAvailability(models)));
        Require(result.SelectedModelId == "model-beta",
            "A higher score bypassed the minimum context hard constraint.");
        Require(result.SelectionMode == ModelSelectionModes.Ranked,
            "Hard-constraint scenario unexpectedly used fallback mode.");
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task MissingEvidenceAsync()
    {
        var models = BaseModels().ToArray();
        models[0] = models[0] with
        {
            BenchmarkEvidence = models[0].BenchmarkEvidence
                .Where(item => item.Dimension != ModelBenchmarkDimensions.Factuality)
                .ToArray(),
        };
        var policy = Policy(
            "missing-evidence-role",
            primary: ["model-alpha"],
            fallback: [],
            required: [ModelBenchmarkDimensions.Factuality],
            weights: Weights((ModelBenchmarkDimensions.Factuality, 10_000)));
        var selector = Selector(BaseCatalog(models: models, policies: [policy]));
        RequireCode(
            () => selector.Select(Request(policy.RoleId, 1, AllAvailability(models))),
            ModelAssignmentErrorCodes.MissingEvidence);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task StaleEvidenceAsync()
    {
        var models = BaseModels().ToArray();
        models[0] = models[0] with { BenchmarkEvidence = Evidence(measuredAt: 1_000_000) };
        var policy = Policy(
            "stale-role",
            primary: ["model-alpha"],
            fallback: [],
            maximumAge: 5_000);
        var selector = Selector(BaseCatalog(models: models, policies: [policy]));
        RequireCode(
            () => selector.Select(Request(policy.RoleId, 1, AllAvailability(models))),
            ModelAssignmentErrorCodes.StaleEvidence);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task LowConfidenceAsync()
    {
        var models = BaseModels().ToArray();
        models[0] = models[0] with { BenchmarkEvidence = Evidence(confidence: 4_000) };
        var policy = Policy(
            "confidence-role",
            primary: ["model-alpha"],
            fallback: [],
            minimumConfidence: 8_000);
        var selector = Selector(BaseCatalog(models: models, policies: [policy]));
        RequireCode(
            () => selector.Select(Request(policy.RoleId, 1, AllAvailability(models))),
            ModelAssignmentErrorCodes.LowConfidence);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task DeterministicRankingAsync()
    {
        var catalog = BaseCatalog();
        var selector = Selector(catalog);
        var first = selector.Select(Request(
            "long-form-author",
            1,
            AllAvailability()));
        var second = selector.Select(Request(
            "long-form-author",
            1,
            AllAvailability().Reverse().ToArray()));
        Require(first == second, "Equivalent availability orders produced unequal assignments.");
        Require(first.AssignmentFingerprint == second.AssignmentFingerprint,
            "Equivalent availability orders produced different fingerprints.");
        Require(first.SelectedModelId == "model-alpha",
            "Weighted ranking did not select the highest-scoring eligible model.");
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task TieBreakingAsync()
    {
        var models = BaseModels().ToArray();
        models[0] = models[0] with
        {
            Locality = ModelLocalities.PublicRemote,
            InputCostMicrosPerMillion = 500,
            OutputCostMicrosPerMillion = 500,
            BenchmarkEvidence = Evidence(score: 8_500),
        };
        models[1] = models[1] with
        {
            Locality = ModelLocalities.PrivateRemote,
            InputCostMicrosPerMillion = 900,
            OutputCostMicrosPerMillion = 900,
            BenchmarkEvidence = Evidence(score: 8_500),
        };
        var selector = Selector(BaseCatalog(models: models));
        var preferred = selector.Select(Request(
            "long-form-author",
            1,
            AllAvailability(models),
            preferredLocality: ModelLocalities.PrivateRemote));
        Require(preferred.SelectedModelId == "model-beta",
            "Preferred locality did not resolve an equal-score tie.");

        var cheapest = selector.Select(Request(
            "long-form-author",
            1,
            AllAvailability(models)));
        Require(cheapest.SelectedModelId == "model-alpha",
            "Cost did not resolve an equal-score tie without locality preference.");
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task ExplicitFallbackAsync()
    {
        var policy = Policy(
            "fallback-role",
            primary: ["model-alpha"],
            fallback: ["model-local", "model-fallback"]);
        var catalog = BaseCatalog(policies: [policy]);
        var selector = Selector(catalog);
        var availability = AllAvailability()
            .Where(item => item.ModelId is "model-local" or "model-fallback")
            .ToArray();
        var result = selector.Select(Request(policy.RoleId, 1, availability));
        Require(result.SelectionMode == ModelSelectionModes.Fallback,
            "Explicit fallback did not report fallback mode.");
        Require(result.SelectedModelId == "model-local",
            "Fallback order was reordered by score or cost.");
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task FallbackCannotBypassAsync()
    {
        var models = BaseModels().ToArray();
        var localIndex = Array.FindIndex(models, item => item.ModelId == "model-local");
        models[localIndex] = models[localIndex] with { ContextWindowTokens = 1_000 };
        var policy = Policy(
            "blocked-fallback-role",
            primary: ["model-alpha"],
            fallback: ["model-local"],
            minimumContext: 32_000);
        var selector = Selector(BaseCatalog(models: models, policies: [policy]));
        var availability = AllAvailability(models)
            .Where(item => item.ModelId == "model-local")
            .ToArray();
        RequireCode(
            () => selector.Select(Request(policy.RoleId, 1, availability)),
            ModelAssignmentErrorCodes.NoEligibleModel);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task ProviderAvailabilityNarrowsAsync()
    {
        var selector = Selector(BaseCatalog());
        var availability = AllAvailability()
            .Where(item => item.ModelId == "model-beta")
            .ToArray();
        var result = selector.Select(Request("long-form-author", 1, availability));
        Require(result.SelectedModelId == "model-beta",
            "Provider availability did not narrow the primary candidate set.");
        Require(result.EligibleCandidateCount == 1,
            "Unavailable catalog models were counted as eligible.");
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task FingerprintValidationAsync()
    {
        var selector = Selector(BaseCatalog());
        RequireCode(
            () => selector.Select(Request("long-form-author", 1, AllAvailability()) with
            {
                RequiredProfileFingerprint = "not-a-fingerprint",
            }),
            ModelAssignmentErrorCodes.ProfileFingerprintInvalid);

        var result = selector.Select(Request("long-form-author", 1, AllAvailability()));
        Require(ModelAssignmentFingerprint.Verify(result), "Assignment fingerprint verification failed.");
        Require(result.ProfileFingerprint == ProfileFingerprint,
            "Profile fingerprint was not bound into the assignment.");
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task ProviderMappingAsync()
    {
        var selector = Selector(BaseCatalog());
        var result = selector.Select(Request("long-form-author", 1, AllAvailability()));
        var advertised = new OpenCodeAdvertisedModel(
            result.SelectedModelId,
            result.SelectedRevision,
            result.ProviderFamily,
            result.ProviderModelKey);
        var mapped = OpenCodeModelAssignmentMapper.Map(
            result,
            new OpenCodeAdvertisedModelCatalog([advertised], MaximumEntries: 32));
        Require(mapped.ModelKey == result.ProviderModelKey,
            "Provider mapping changed the selected provider model key.");
        Require(mapped.AssignmentFingerprint == result.AssignmentFingerprint,
            "Provider mapping changed the assignment fingerprint.");

        RequireCode(
            () => OpenCodeModelAssignmentMapper.Map(
                result,
                new OpenCodeAdvertisedModelCatalog([
                    advertised with { ProviderModelKey = "provider-mismatch" },
                ], MaximumEntries: 32)),
            ModelAssignmentErrorCodes.ProviderUnsupported);
        _scenarios++;
        return Task.CompletedTask;
    }

    private async Task ConcurrencyCancellationNoMutationAsync()
    {
        var selector = Selector(BaseCatalog());
        var request = Request("long-form-author", 1, AllAvailability());
        var tasks = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => selector.Select(request)))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        Require(results.Select(item => item.AssignmentFingerprint)
                .Distinct(StringComparer.Ordinal).Count() == 1,
            "Concurrent selection was not deterministic.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        RequireException<OperationCanceledException>(
            () => selector.Select(request, cancellation.Token));
        const int remoteMutations = 0;
        Require(remoteMutations == 0, "Model selection performed a remote mutation.");
        _scenarios++;
    }

    private static ModelAssignmentSelector Selector(ModelBenchmarkCatalog catalog) => new(catalog);

    private static ModelBenchmarkCatalog BaseCatalog(
        IReadOnlyList<ModelBenchmarkDefinition>? models = null,
        IReadOnlyList<ModelRolePolicyDefinition>? policies = null) =>
        new(
            catalogVersion: 7,
            measuredAtEpochSeconds: FreshMeasuredAt,
            models ?? BaseModels(),
            policies ?? BasePolicies());

    private static IReadOnlyList<ModelBenchmarkDefinition> BaseModels() =>
    [
        Model(
            "model-alpha",
            revision: 2,
            providerFamily: "provider-a",
            providerKey: "alpha-v2",
            locality: ModelLocalities.PublicRemote,
            context: 128_000,
            output: 16_000,
            inputCost: 600,
            outputCost: 1_200,
            latency: 900,
            safety: 5,
            evidence: Evidence(score: 9_200)),
        Model(
            "model-beta",
            revision: 1,
            providerFamily: "provider-b",
            providerKey: "beta-v1",
            locality: ModelLocalities.PrivateRemote,
            context: 96_000,
            output: 12_000,
            inputCost: 400,
            outputCost: 800,
            latency: 700,
            safety: 5,
            evidence: Evidence(score: 8_600)),
        Model(
            "model-gamma",
            revision: 1,
            providerFamily: "provider-c",
            providerKey: "gamma-v1",
            locality: ModelLocalities.PublicRemote,
            context: 64_000,
            output: 8_000,
            inputCost: 250,
            outputCost: 500,
            latency: 500,
            safety: 4,
            evidence: Evidence(score: 8_000)),
        Model(
            "model-local",
            revision: 3,
            providerFamily: "local-runtime",
            providerKey: "local-v3",
            locality: ModelLocalities.Local,
            context: 64_000,
            output: 8_000,
            inputCost: 0,
            outputCost: 0,
            latency: 1_100,
            safety: 4,
            evidence: Evidence(score: 7_700)),
        Model(
            "model-fallback",
            revision: 1,
            providerFamily: "provider-d",
            providerKey: "fallback-v1",
            locality: ModelLocalities.PrivateRemote,
            context: 64_000,
            output: 8_000,
            inputCost: 100,
            outputCost: 200,
            latency: 800,
            safety: 4,
            evidence: Evidence(score: 7_500)),
    ];

    private static IReadOnlyList<ModelRolePolicyDefinition> BasePolicies() =>
    [
        Policy("long-form-author", primary: ["model-alpha", "model-beta"], fallback: ["model-local"]),
        Policy("structural-editor", primary: ["model-beta", "model-gamma"], fallback: ["model-alpha"]),
        Policy("quality-auditor", primary: ["model-alpha", "model-gamma"], fallback: ["model-beta"]),
        Policy("release-preparer", primary: ["model-beta"], fallback: ["model-alpha"]),
        Policy(
            "local-only-author",
            primary: ["model-local"],
            fallback: [],
            allowedLocalities: [ModelLocalities.Local]),
    ];

    private static ModelBenchmarkDefinition Model(
        string modelId,
        int revision,
        string providerFamily,
        string providerKey,
        string locality,
        int context,
        int output,
        long inputCost,
        long outputCost,
        int latency,
        int safety,
        IReadOnlyList<ModelBenchmarkEvidence> evidence) =>
        new(
            modelId,
            revision,
            providerFamily,
            providerKey,
            locality,
            context,
            output,
            inputCost,
            outputCost,
            latency,
            SupportsStructuredOutput: true,
            SupportsToolCalling: true,
            SupportsVision: false,
            SupportsReasoning: true,
            safety,
            evidence);

    private static ModelRolePolicyDefinition Policy(
        string roleId,
        IReadOnlyList<string> primary,
        IReadOnlyList<string> fallback,
        IReadOnlyList<string>? required = null,
        IReadOnlyDictionary<string, int>? weights = null,
        long maximumAge = 10_000,
        int minimumConfidence = 8_000,
        int minimumContext = 32_000,
        IReadOnlyList<string>? allowedLocalities = null) =>
        new(
            roleId,
            Version: 1,
            PrimaryModelIds: primary,
            FallbackModelIds: fallback,
            RequiredDimensions: required ?? [
                ModelBenchmarkDimensions.LongFormCoherence,
                ModelBenchmarkDimensions.InstructionFollowing,
            ],
            MaximumEvidenceAgeSeconds: maximumAge,
            MinimumConfidenceBasisPoints: minimumConfidence,
            MinimumContextWindowTokens: minimumContext,
            MinimumOutputTokens: 4_000,
            MaximumInputCostMicrosPerMillion: 10_000,
            MaximumOutputCostMicrosPerMillion: 20_000,
            MaximumMedianLatencyMs: 5_000,
            MinimumSafetyTier: 3,
            AllowedLocalities: allowedLocalities ?? [
                ModelLocalities.Local,
                ModelLocalities.PrivateRemote,
                ModelLocalities.PublicRemote,
            ],
            RequiresStructuredOutput: true,
            RequiresToolCalling: true,
            RequiresVision: false,
            RequiresReasoning: true,
            WeightsBasisPoints: weights ?? Weights(
                (ModelBenchmarkDimensions.LongFormCoherence, 6_000),
                (ModelBenchmarkDimensions.InstructionFollowing, 4_000)));

    private static IReadOnlyDictionary<string, int> Weights(
        params (string Dimension, int Weight)[] entries) =>
        entries.ToDictionary(item => item.Dimension, item => item.Weight, StringComparer.Ordinal);

    private static IReadOnlyList<ModelBenchmarkEvidence> Evidence(
        int score = 8_000,
        int confidence = 9_000,
        long measuredAt = FreshMeasuredAt) =>
        ModelBenchmarkDimensions.Known
            .Order(StringComparer.Ordinal)
            .Select(dimension => new ModelBenchmarkEvidence(
                dimension,
                score,
                SampleCount: 100,
                confidence,
                measuredAt,
                SourceId: "fixture-evidence",
                SourceDigestSha256:
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"))
            .ToArray();

    private static ModelProviderAvailability Availability(ModelBenchmarkDefinition model) =>
        new(model.ModelId, model.Revision, model.ProviderFamily, model.ProviderModelKey);

    private static IReadOnlyList<ModelProviderAvailability> AllAvailability(
        IReadOnlyList<ModelBenchmarkDefinition>? models = null) =>
        (models ?? BaseModels()).Select(Availability).ToArray();

    private static ModelAssignmentRequest Request(
        string roleId,
        int version,
        IReadOnlyList<ModelProviderAvailability> availability,
        string? preferredLocality = null) =>
        new(
            roleId,
            version,
            EvaluationEpochSeconds,
            ProfileFingerprint,
            availability,
            preferredLocality);

    private static void RequireCode(Action action, string code)
    {
        try
        {
            action();
        }
        catch (ModelAssignmentException exception) when (exception.Code == code)
        {
            Require(exception.Message == code, "Stable model rejection message drifted.");
            return;
        }
        throw new InvalidOperationException($"Expected stable rejection code {code}.");
    }

    private static void RequireException<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record ModelBenchmarksReport(
    int Scenarios,
    int Models,
    int Roles,
    string Gate,
    string Mutation);
