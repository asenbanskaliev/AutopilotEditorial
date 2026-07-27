using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Prompts;

/// <summary>Adds versioned prompts and their static resource to an existing bounded MCP router.</summary>
public sealed class PromptEnabledFeatureRouter : IMcpFeatureRouter, IAsyncDisposable
{
    private readonly IMcpFeatureRouter _inner;
    private readonly VersionedMcpPromptCatalog _prompts;
    private readonly IReadOnlyList<McpResourceDefinition> _resources;
    private readonly string _resourceCursorScope;
    private readonly string _resourceFingerprint;
    private readonly int _resourcePageSize;
    private readonly IReadOnlyDictionary<string, object> _capabilities;
    private int _disposed;

    public PromptEnabledFeatureRouter(
        IMcpFeatureRouter inner,
        VersionedMcpPromptCatalog prompts,
        IEnumerable<McpResourceDefinition> listedResources,
        string resourceCursorScope,
        int resourcePageSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(listedResources);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceCursorScope);
        if (resourcePageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(resourcePageSize));
        }

        var resources = listedResources
            .Concat(prompts.Resources)
            .OrderBy(resource => resource.Uri, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0 ||
            resources.Select(resource => resource.Uri)
                .Distinct(StringComparer.Ordinal).Count() != resources.Length)
        {
            throw new ArgumentException(
                "Prompt-enabled resource catalog is empty or contains duplicate URIs.",
                nameof(listedResources));
        }

        _inner = inner;
        _prompts = prompts;
        _resources = resources;
        _resourceCursorScope = resourceCursorScope;
        _resourcePageSize = resourcePageSize;
        _resourceFingerprint = Fingerprint(resources.Select(resource => resource.Uri));
        _capabilities = MergeCapabilities(inner.Capabilities);
    }

    public IReadOnlyDictionary<string, object> Capabilities => _capabilities;

    public string Instructions =>
        _inner.Instructions +
        " Versioned prompts are user-controlled templates; retrieving a prompt does not execute tools or call a model.";

    public async ValueTask<McpDispatchResult?> TryDispatchAsync(
        string method,
        JsonElement? parameters,
        JsonElement requestId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();

        var promptResult = McpPromptDispatcher.TryDispatch(
            method,
            parameters,
            requestId,
            _prompts);
        if (promptResult is not null)
        {
            return promptResult;
        }

        if (method == "resources/list")
        {
            return HandleResourcesList(parameters, requestId);
        }
        if (method == "resources/read")
        {
            var promptResource = HandlePromptResourceRead(parameters, requestId);
            if (promptResource is not null)
            {
                return promptResource;
            }
        }

        return await _inner.TryDispatchAsync(
                method,
                parameters,
                requestId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_inner is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private McpDispatchResult HandleResourcesList(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (!TryReadResourceCursor(parameters, out var offset, out var error))
        {
            return InvalidParams(requestId, error!);
        }
        if (offset > _resources.Count)
        {
            return InvalidParams(
                requestId,
                "Cursor offset exceeds the resource catalog.");
        }

        var resources = _resources
            .Skip(offset)
            .Take(_resourcePageSize)
            .ToArray();
        var nextOffset = offset + resources.Length;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["resources"] = resources,
        };
        if (nextOffset < _resources.Count)
        {
            result["nextCursor"] = McpCursorCodec.Encode(
                _resourceCursorScope,
                nextOffset,
                _resourceFingerprint);
        }
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(requestId, result));
    }

    private McpDispatchResult? HandlePromptResourceRead(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (parameters is null ||
            !HasOnlyProperties(parameters.Value, "uri") ||
            !parameters.Value.TryGetProperty("uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri) ||
            uri.Length > 512 ||
            !_prompts.TryGetResource(uri, out var prompt))
        {
            return null;
        }

        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new
                {
                    contents = new[]
                    {
                        new McpResourceContent(
                            prompt.ResourceUri,
                            VersionedMcpPrompt.ResourceMediaType,
                            prompt.ResourceJson,
                            Blob: null),
                    },
                }));
    }

    private bool TryReadResourceCursor(
        JsonElement? parameters,
        out int offset,
        out string? error)
    {
        offset = 0;
        error = null;
        if (parameters is null)
        {
            return true;
        }
        if (!HasOnlyProperties(parameters.Value, "cursor"))
        {
            error = "resources/list params may contain only cursor.";
            return false;
        }
        if (!parameters.Value.TryGetProperty("cursor", out var cursorElement))
        {
            return true;
        }
        if (cursorElement.ValueKind != JsonValueKind.String)
        {
            error = "cursor must be a string.";
            return false;
        }

        try
        {
            offset = McpCursorCodec.Decode(
                cursorElement.GetString() ?? string.Empty,
                _resourceCursorScope,
                _resourceFingerprint);
            return true;
        }
        catch (McpCursorException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static IReadOnlyDictionary<string, object> MergeCapabilities(
        IReadOnlyDictionary<string, object> inner)
    {
        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var capability in inner)
        {
            if (string.Equals(capability.Key, "prompts", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The wrapped router already advertises prompts.",
                    nameof(inner));
            }
            result.Add(capability.Key, capability.Value);
        }
        result.Add(
            "prompts",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["listChanged"] = false,
            });
        return result;
    }

    private static bool HasOnlyProperties(
        JsonElement source,
        params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return source.ValueKind == JsonValueKind.Object &&
               source.EnumerateObject().All(property => set.Contains(property.Name));
    }

    private static McpDispatchResult InvalidParams(
        JsonElement requestId,
        string message) =>
        new(
            JsonRpcMessageWriter.Error(
                requestId,
                JsonRpcErrorCodes.InvalidParams,
                message),
            "MCP_INVALID_PARAMS");

    private static string Fingerprint(IEnumerable<string> identifiers)
    {
        var canonical = string.Join('\n', identifiers);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
