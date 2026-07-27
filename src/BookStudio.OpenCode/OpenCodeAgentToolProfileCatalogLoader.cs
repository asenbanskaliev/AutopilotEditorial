using System.Text.Json;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

public static class OpenCodeAgentToolProfileCatalogLoader
{
    public const int MaximumPayloadBytes = 512 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private static readonly IReadOnlySet<string> RootProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "catalogVersion",
            "profiles",
        };

    private static readonly IReadOnlySet<string> ProfileProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "profileId",
            "version",
            "workflow",
            "role",
            "allowedCapabilities",
            "allowedTools",
            "forbiddenCapabilities",
            "forbiddenTools",
            "requiresHumanApproval",
            "maximumToolCalls",
            "maximumParallelTools",
        };

    public static AgentToolProfileCatalog Load(ReadOnlyMemory<byte> payload)
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

            var catalogVersion = ReadPositiveInt32(root, "catalogVersion");
            if (!root.TryGetProperty("profiles", out var profilesElement) ||
                profilesElement.ValueKind != JsonValueKind.Array ||
                profilesElement.GetArrayLength() is < 1 or > AgentToolProfileCatalog.MaximumProfiles)
            {
                throw Invalid();
            }

            var profiles = new List<AgentToolProfileDefinition>(profilesElement.GetArrayLength());
            foreach (var element in profilesElement.EnumerateArray())
            {
                RequireObject(element);
                EnsureUniqueProperties(element);
                EnsureAllowedProperties(element, ProfileProperties);
                profiles.Add(new AgentToolProfileDefinition(
                    ReadString(element, "profileId"),
                    ReadPositiveInt32(element, "version"),
                    ReadString(element, "workflow"),
                    ReadString(element, "role"),
                    ReadStringArray(element, "allowedCapabilities"),
                    ReadStringArray(element, "allowedTools"),
                    ReadStringArray(element, "forbiddenCapabilities"),
                    ReadStringArray(element, "forbiddenTools"),
                    ReadBoolean(element, "requiresHumanApproval"),
                    ReadPositiveInt32(element, "maximumToolCalls"),
                    ReadPositiveInt32(element, "maximumParallelTools")));
            }

            return new AgentToolProfileCatalog(catalogVersion, profiles);
        }
        catch (AgentToolProfileException)
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

    private static int ReadPositiveInt32(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            !value.TryGetInt32(out var result) ||
            result < 1)
        {
            throw Invalid();
        }
        return result;
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

    private static IReadOnlyList<string> ReadStringArray(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() > AgentToolProfileCatalog.MaximumEntriesPerList)
        {
            throw Invalid();
        }
        var result = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid();
            }
            result.Add(item.GetString() ?? string.Empty);
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

    private static AgentToolProfileException Invalid() =>
        new(AgentToolProfileErrorCodes.Invalid);
}
