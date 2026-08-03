using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed class StructuredPreflightEditorialArtifactGateway : IEditorialArtifactGateway, IAsyncDisposable
{
    private readonly StdioMcpEditorialArtifactGateway _inner;
    private readonly StdioMcpEditorialGatewayOptions _options;

    public StructuredPreflightEditorialArtifactGateway(StdioMcpEditorialGatewayOptions options, string receiptConnectionString)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _inner = new StdioMcpEditorialArtifactGateway(options, receiptConnectionString);
    }

    public ValueTask<PersistedEditorialArtifact?> GetAsync(string projectId, string artifactId, int version, CancellationToken cancellationToken) =>
        _inner.GetAsync(projectId, artifactId, version, cancellationToken);

    public ValueTask<PersistedEditorialArtifact> RegisterAsync(string projectId, string artifactId, int expectedVersion, string mediaType, string content, CancellationToken cancellationToken) =>
        _inner.RegisterAsync(projectId, artifactId, expectedVersion, mediaType, content, cancellationToken);

    public ValueTask<EditorialValidationResult> ValidateAsync(string projectId, string artifactId, int version, CancellationToken cancellationToken) =>
        _inner.ValidateAsync(projectId, artifactId, version, cancellationToken);

    public ValueTask<EditorialReleaseResult?> GetReleaseAsync(string projectId, string releaseArtifactId, int version, CancellationToken cancellationToken) =>
        _inner.GetReleaseAsync(projectId, releaseArtifactId, version, cancellationToken);

    public ValueTask<EditorialReleaseResult> PrepareReleaseAsync(string projectId, string releaseId, string title, string language, PersistedEditorialArtifact manuscript, CancellationToken cancellationToken) =>
        _inner.PrepareReleaseAsync(projectId, releaseId, title, language, manuscript, cancellationToken);

    public async ValueTask<EditorialPreflightResult> PreflightAsync(string projectId, EditorialReleaseResult release, CancellationToken cancellationToken)
    {
        await using var session = await McpStdioSession.StartAsync(
            _options.ProductionExecutable,
            _options.WorkingDirectory,
            _options.WorkspaceRoot,
            _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);

        var result = await session.CallToolAsync(
            "book.preflight.run",
            new
            {
                projectId,
                payload = new
                {
                    releaseArtifactId = release.ReleaseArtifactId,
                    version = release.Version,
                    profile = "release-basic",
                },
            },
            cancellationToken).ConfigureAwait(false);

        var normalized = Normalize(result);
        var blockers = FindBlockingReasons(normalized).Distinct(StringComparer.Ordinal).ToArray();
        var passed = blockers.Length == 0 && FindPassSignal(normalized);
        return new EditorialPreflightResult(passed, passed ? [] : blockers.Length == 0 ? ["preflight_pass_signal_missing"] : blockers);
    }

    private static JsonElement Normalize(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var structured)) return structured.Clone();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("text", out var textElement)) continue;
                var text = textElement.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return JsonSerializer.SerializeToElement(new { status = text.Trim() });
                }
            }
        }
        return result.Clone();
    }

    private static bool FindPassSignal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (property.Value.ValueKind is JsonValueKind.True &&
                    (name.Equals("passed", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("valid", StringComparison.OrdinalIgnoreCase))) return true;

                if (property.Value.ValueKind == JsonValueKind.String &&
                    (name.Equals("status", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("verdict", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("outcome", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("decision", StringComparison.OrdinalIgnoreCase)))
                {
                    var value = property.Value.GetString()?.Trim();
                    if (value is not null && (value.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
                                              value.Equals("PASSED", StringComparison.OrdinalIgnoreCase) ||
                                              value.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                                              value.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                                              value.Equals("READY", StringComparison.OrdinalIgnoreCase))) return true;
                }
                if (FindPassSignal(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) if (FindPassSignal(item)) return true;
        }
        return false;
    }

    private static IEnumerable<string> FindBlockingReasons(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var blockingField = property.Name.Equals("blockingReasons", StringComparison.OrdinalIgnoreCase) ||
                                    property.Name.Equals("blockers", StringComparison.OrdinalIgnoreCase) ||
                                    property.Name.Equals("blocking", StringComparison.OrdinalIgnoreCase) ||
                                    property.Name.Equals("errors", StringComparison.OrdinalIgnoreCase);
                if (blockingField)
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            var reason = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                            if (!string.IsNullOrWhiteSpace(reason)) yield return reason!;
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        yield return property.Value.GetString()!;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.True)
                    {
                        yield return property.Name;
                    }
                }
                foreach (var nested in FindBlockingReasons(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in FindBlockingReasons(item)) yield return nested;
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
