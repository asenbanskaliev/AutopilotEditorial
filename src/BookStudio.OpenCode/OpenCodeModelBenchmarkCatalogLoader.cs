using System.Text.Json;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

public static class OpenCodeModelBenchmarkCatalogLoader
{
    public const int MaximumPayloadBytes = 2 * 1024 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 48,
    };

    private static readonly IReadOnlySet<string> RootProperties = Set(
        "schemaVersion",
        "catalogVersion",
        "measuredAtEpochSeconds",
        "models",
        "rolePolicies");

    private static readonly IReadOnlySet<string> ModelProperties = Set(
        "modelId",
        "revision",
        "providerFamily",
        "providerModelKey",
        "locality",
        "contextWindowTokens",
        "maximumOutputTokens",
        "inputCostMicrosPerMillion",
        "outputCostMicrosPerMillion",
        "medianLatencyMs",
        "supportsStructuredOutput",
        "supportsToolCalling",
        "supportsVision",
        "supportsReasoning",
        "safetyTier",
        "benchmarkEvidence");

    private static readonly IReadOnlySet<string> EvidenceProperties = Set(
        "dimension",
        "scoreBasisPoints",
        "sampleCount",
        "confidenceBasisPoints",
        "measuredAtEpochSeconds",
        "sourceId",
        "sourceDigestSha256");

    private static readonly IReadOnlySet<string> PolicyProperties = Set(
        "roleId",
        "version",
        "primaryModelIds",
        "fallbackModelIds",
        "requiredDimensions",
        "maximumEvidenceAgeSeconds",
        "minimumConfidenceBasisPoints",
        "minimumContextWindowTokens",
        "minimumOutputTokens",
        "maximumInputCostMicrosPerMillion",
        "maximumOutputCostMicrosPerMillion",
        "maximumMedianLatencyMs",
        "minimumSafetyTier",
        "allowedLocalities",
        "requiresStructuredOutput",
        "requiresToolCalling",
        "requiresVision",
        "requiresReasoning",
        "weightsBasisPoints");

    public static ModelBenchmarkCatalog Load(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > MaximumPayloadBytes)
        {
            throw Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(payload, JsonOptions);
            var root = document.RootElement;
            RequireObject(root);
            EnsureUniqueProperties(root);
            EnsureAllowedProperties(root, RootProperties);

            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.String ||
                !string.Equals(schemaVersion.GetString(), "1.0.0", StringComparison.Ordinal))
            {
                throw Invalid();
            }

            var models = ReadModels(root);
            var policies = ReadPolicies(root);
            return new ModelBenchmarkCatalog(
                ReadPositiveInt32(root, "catalogVersion"),
                ReadNonNegativeInt64(root, "measuredAtEpochSeconds"),
                models,
                policies);
        }
        catch (ModelAssignmentException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        catch (InvalidOperationException)
        {
            throw Invalid();
        }
        catch (OverflowException)
        {
            throw Invalid();
        }
    }

    private static IReadOnlyList<ModelBenchmarkDefinition> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() is < 1 or > ModelBenchmarkCatalog.MaximumModels)
        {
            throw Invalid();
        }

        var result = new List<ModelBenchmarkDefinition>(array.GetArrayLength());
        foreach (var source in array.EnumerateArray())
        {
            RequireObject(source);
            EnsureUniqueProperties(source);
            EnsureAllowedProperties(source, ModelProperties);
            result.Add(new ModelBenchmarkDefinition(
                ReadString(source, "modelId"),
                ReadPositiveInt32(source, "revision"),
                ReadString(source, "providerFamily"),
                ReadString(source, "providerModelKey"),
                ReadString(source, "locality"),
                ReadPositiveInt32(source, "contextWindowTokens"),
                ReadPositiveInt32(source, "maximumOutputTokens"),
                ReadNonNegativeInt64(source, "inputCostMicrosPerMillion"),
                ReadNonNegativeInt64(source, "outputCostMicrosPerMillion"),
                ReadPositiveInt32(source, "medianLatencyMs"),
                ReadBoolean(source, "supportsStructuredOutput"),
                ReadBoolean(source, "supportsToolCalling"),
                ReadBoolean(source, "supportsVision"),
                ReadBoolean(source, "supportsReasoning"),
                ReadPositiveInt32(source, "safetyTier"),
                ReadEvidence(source)));
        }
        return result;
    }

    private static IReadOnlyList<ModelBenchmarkEvidence> ReadEvidence(JsonElement model)
    {
        if (!model.TryGetProperty("benchmarkEvidence", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() is < 1 or > ModelBenchmarkCatalog.MaximumEvidenceEntries)
        {
            throw Invalid();
        }

        var result = new List<ModelBenchmarkEvidence>(array.GetArrayLength());
        foreach (var source in array.EnumerateArray())
        {
            RequireObject(source);
            EnsureUniqueProperties(source);
            EnsureAllowedProperties(source, EvidenceProperties);
            result.Add(new ModelBenchmarkEvidence(
                ReadString(source, "dimension"),
                ReadNonNegativeInt32(source, "scoreBasisPoints"),
                ReadPositiveInt32(source, "sampleCount"),
                ReadNonNegativeInt32(source, "confidenceBasisPoints"),
                ReadNonNegativeInt64(source, "measuredAtEpochSeconds"),
                ReadString(source, "sourceId"),
                ReadString(source, "sourceDigestSha256")));
        }
        return result;
    }

    private static IReadOnlyList<ModelRolePolicyDefinition> ReadPolicies(JsonElement root)
    {
        if (!root.TryGetProperty("rolePolicies", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() is < 1 or > ModelBenchmarkCatalog.MaximumRolePolicies)
        {
            throw Invalid();
        }

        var result = new List<ModelRolePolicyDefinition>(array.GetArrayLength());
        foreach (var source in array.EnumerateArray())
        {
            RequireObject(source);
            EnsureUniqueProperties(source);
            EnsureAllowedProperties(source, PolicyProperties);
            result.Add(new ModelRolePolicyDefinition(
                ReadString(source, "roleId"),
                ReadPositiveInt32(source, "version"),
                ReadStringArray(source, "primaryModelIds"),
                ReadStringArray(source, "fallbackModelIds"),
                ReadStringArray(source, "requiredDimensions"),
                ReadNonNegativeInt64(source, "maximumEvidenceAgeSeconds"),
                ReadNonNegativeInt32(source, "minimumConfidenceBasisPoints"),
                ReadPositiveInt32(source, "minimumContextWindowTokens"),
                ReadPositiveInt32(source, "minimumOutputTokens"),
                ReadNonNegativeInt64(source, "maximumInputCostMicrosPerMillion"),
                ReadNonNegativeInt64(source, "maximumOutputCostMicrosPerMillion"),
                ReadPositiveInt32(source, "maximumMedianLatencyMs"),
                ReadPositiveInt32(source, "minimumSafetyTier"),
                ReadStringArray(source, "allowedLocalities"),
                ReadBoolean(source, "requiresStructuredOutput"),
                ReadBoolean(source, "requiresToolCalling"),
                ReadBoolean(source, "requiresVision"),
                ReadBoolean(source, "requiresReasoning"),
                ReadWeights(source)));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, int> ReadWeights(JsonElement policy)
    {
        if (!policy.TryGetProperty("weightsBasisPoints", out var source) ||
            source.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
        EnsureUniqueProperties(source);
        if (source.GetRawText().Length > MaximumPayloadBytes)
        {
            throw Invalid();
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            if (result.Count >= ModelBenchmarkCatalog.MaximumListEntries ||
                !property.Value.TryGetInt32(out var weight))
            {
                throw Invalid();
            }
            result.Add(property.Name, weight);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() > ModelBenchmarkCatalog.MaximumListEntries)
        {
            throw Invalid();
        }

        var result = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid();
            }
            result.Add(item.GetString() ?? string.Empty);
        }
        return result;
    }

    private static string ReadString(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }
        return value.GetString() ?? string.Empty;
    }

    private static bool ReadBoolean(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid();
        }
        return value.GetBoolean();
    }

    private static int ReadPositiveInt32(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            !value.TryGetInt32(out var result) || result < 1)
        {
            throw Invalid();
        }
        return result;
    }

    private static int ReadNonNegativeInt32(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            !value.TryGetInt32(out var result) || result < 0)
        {
            throw Invalid();
        }
        return result;
    }

    private static long ReadNonNegativeInt64(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            !value.TryGetInt64(out var result) || result < 0)
        {
            throw Invalid();
        }
        return result;
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
    }

    private static void EnsureUniqueProperties(JsonElement source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw Invalid();
            }
        }
    }

    private static void EnsureAllowedProperties(
        JsonElement source,
        IReadOnlySet<string> allowed)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid();
            }
        }
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static ModelAssignmentException Invalid() =>
        new(ModelAssignmentErrorCodes.Invalid);
}
