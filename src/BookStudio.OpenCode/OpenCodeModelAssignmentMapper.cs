using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

public sealed record OpenCodeAdvertisedModel(
    string ModelId,
    int Revision,
    string ProviderFamily,
    string ProviderModelKey);

public sealed record OpenCodeAdvertisedModelCatalog(
    IReadOnlyList<OpenCodeAdvertisedModel> AdvertisedModels,
    int MaximumEntries);

public sealed class OpenCodeMappedModelAssignment
{
    internal OpenCodeMappedModelAssignment(
        string providerFamily,
        string modelKey,
        string assignmentFingerprint)
    {
        ProviderFamily = providerFamily;
        ModelKey = modelKey;
        AssignmentFingerprint = assignmentFingerprint;
    }

    public string ProviderFamily { get; }
    public string ModelKey { get; }
    public string AssignmentFingerprint { get; }
}

public static class OpenCodeModelAssignmentMapper
{
    public static OpenCodeMappedModelAssignment Map(
        ModelAssignmentDecision assignment,
        OpenCodeAdvertisedModelCatalog providerCatalog)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(providerCatalog);

        if (!ModelAssignmentFingerprint.Verify(assignment) ||
            providerCatalog.MaximumEntries is < 1 or > 4096 ||
            providerCatalog.AdvertisedModels is null ||
            providerCatalog.AdvertisedModels.Count > providerCatalog.MaximumEntries)
        {
            throw ProviderUnsupported();
        }

        var seen = new HashSet<ModelKey>();
        OpenCodeAdvertisedModel? exact = null;
        foreach (var source in providerCatalog.AdvertisedModels)
        {
            if (source is null || source.Revision < 1)
            {
                throw ProviderUnsupported();
            }

            string modelId;
            string providerFamily;
            string providerModelKey;
            try
            {
                modelId = ModelBenchmarkCatalog.ValidateIdentifier(source.ModelId);
                providerFamily = ModelBenchmarkCatalog.ValidateIdentifier(source.ProviderFamily);
                providerModelKey = ModelBenchmarkCatalog.ValidateIdentifier(source.ProviderModelKey);
            }
            catch (ModelAssignmentException)
            {
                throw ProviderUnsupported();
            }

            var key = new ModelKey(modelId, source.Revision);
            if (!seen.Add(key))
            {
                throw ProviderUnsupported();
            }

            if (string.Equals(modelId, assignment.SelectedModelId, StringComparison.Ordinal) &&
                source.Revision == assignment.SelectedRevision)
            {
                exact = new OpenCodeAdvertisedModel(
                    modelId,
                    source.Revision,
                    providerFamily,
                    providerModelKey);
            }
        }

        if (exact is null ||
            !string.Equals(exact.ProviderFamily, assignment.ProviderFamily, StringComparison.Ordinal) ||
            !string.Equals(exact.ProviderModelKey, assignment.ProviderModelKey, StringComparison.Ordinal))
        {
            throw ProviderUnsupported();
        }

        return new OpenCodeMappedModelAssignment(
            assignment.ProviderFamily,
            assignment.ProviderModelKey,
            assignment.AssignmentFingerprint);
    }

    private static ModelAssignmentException ProviderUnsupported() =>
        new(ModelAssignmentErrorCodes.ProviderUnsupported);

    private readonly record struct ModelKey(string ModelId, int Revision);
}
