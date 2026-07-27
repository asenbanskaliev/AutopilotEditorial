using System.Text.Json;
using System.Text.RegularExpressions;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

/// <summary>Extracts the BookStudio-required feature matrix from a bounded OpenAPI 3.x document.</summary>
public static partial class OpenCodeOpenApiInspector
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    private static readonly IReadOnlyList<RequiredOperation> RequiredOperations =
    [
        new(OpenCodeFeatureIds.ProvidersList, "get", "/provider"),
        new(OpenCodeFeatureIds.AgentsList, "get", "/agent"),
        new(OpenCodeFeatureIds.McpStatus, "get", "/mcp"),
        new(OpenCodeFeatureIds.SessionsList, "get", "/session"),
        new(OpenCodeFeatureIds.SessionsCreate, "post", "/session"),
        new(OpenCodeFeatureIds.SessionsGet, "get", "/session/{id}"),
        new(OpenCodeFeatureIds.SessionsStatus, "get", "/session/status"),
        new(OpenCodeFeatureIds.SessionsPromptAsync, "post", "/session/{id}/prompt_async"),
        new(OpenCodeFeatureIds.SessionsAbort, "post", "/session/{id}/abort"),
        new(OpenCodeFeatureIds.EventsProject, "get", "/event"),
        new(OpenCodeFeatureIds.EventsGlobal, "get", "/global/event"),
    ];

    public static OpenCodeOpenApiInspection Inspect(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new OpenCodeOpenApiException("OpenAPI document is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OpenCodeOpenApiException("OpenAPI document root must be an object.");
            }
            EnsureUniqueProperties(root, "OpenAPI root");
            if (!root.TryGetProperty("openapi", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                throw new OpenCodeOpenApiException("OpenAPI version is missing.");
            }
            var version = versionElement.GetString() ?? string.Empty;
            if (!version.StartsWith("3.", StringComparison.Ordinal) ||
                version.Length > 32 ||
                version.Any(char.IsControl))
            {
                throw new OpenCodeOpenApiException("Only bounded OpenAPI 3.x documents are supported.");
            }
            if (!root.TryGetProperty("paths", out var paths) ||
                paths.ValueKind != JsonValueKind.Object)
            {
                throw new OpenCodeOpenApiException("OpenAPI paths are missing.");
            }
            EnsureUniqueProperties(paths, "OpenAPI paths");

            var operations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pathProperty in paths.EnumerateObject())
            {
                if (pathProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var normalizedPath = NormalizePath(pathProperty.Name);
                EnsureUniqueProperties(pathProperty.Value, "OpenAPI path item");
                foreach (var operation in pathProperty.Value.EnumerateObject())
                {
                    var method = operation.Name.ToLowerInvariant();
                    if (method is "get" or "post" or "put" or "patch" or "delete" or "head" or "options")
                    {
                        operations.Add(method + " " + normalizedPath);
                    }
                }
            }

            var features = RequiredOperations
                .Where(required => operations.Contains(required.Method + " " + required.Path))
                .Select(required => required.FeatureId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return new OpenCodeOpenApiInspection(version, features);
        }
        catch (OpenCodeOpenApiException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new OpenCodeOpenApiException("OpenAPI JSON is malformed.", exception);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 512 ||
            path.Any(char.IsControl) ||
            !path.StartsWith("/", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var normalized = ColonParameterRegex().Replace(path, "/{id}");
        normalized = BracedParameterRegex().Replace(normalized, "{id}");
        return normalized;
    }

    private static void EnsureUniqueProperties(JsonElement source, string scope)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new OpenCodeOpenApiException(scope + " contains a duplicate property.");
            }
        }
    }

    [GeneratedRegex("/:[A-Za-z_][A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex ColonParameterRegex();

    [GeneratedRegex("\\{[^{}\\r\\n]{1,128}\\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracedParameterRegex();

    private sealed record RequiredOperation(string FeatureId, string Method, string Path);
}

public sealed record OpenCodeOpenApiInspection(
    string Version,
    IReadOnlyList<string> DetectedFeatures);

public sealed class OpenCodeOpenApiException : Exception
{
    public OpenCodeOpenApiException(string message) : base(message) { }

    public OpenCodeOpenApiException(string message, Exception innerException)
        : base(message, innerException) { }
}
