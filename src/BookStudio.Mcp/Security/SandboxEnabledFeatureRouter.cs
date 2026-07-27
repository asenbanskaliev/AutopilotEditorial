using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Security;

/// <summary>Adds the effective sandbox policy resource to one bounded MCP router.</summary>
public sealed class SandboxEnabledFeatureRouter : IMcpFeatureRouter, IAsyncDisposable
{
    private readonly IMcpFeatureRouter _inner;
    private readonly McpSandboxPolicyResource _policy;
    private readonly string _resourceCursorScope;
    private readonly string _resourceFingerprint;
    private readonly int _resourcePageSize;
    private int _disposed;

    public SandboxEnabledFeatureRouter(
        IMcpFeatureRouter inner,
        McpHostOptions options,
        IEnumerable<McpResourceDefinition> listedResources,
        string resourceCursorScope,
        int resourcePageSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(listedResources);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceCursorScope);
        if (resourcePageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(resourcePageSize));
        }

        _policy = new McpSandboxPolicyResource(options);
        Resources = listedResources
            .Append(_policy.Definition)
            .OrderBy(resource => resource.Uri, StringComparer.Ordinal)
            .ToArray();
        if (Resources.Select(resource => resource.Uri)
                .Distinct(StringComparer.Ordinal).Count() != Resources.Count)
        {
            throw new ArgumentException("Sandbox-enabled resources contain duplicate URIs.", nameof(listedResources));
        }

        _inner = inner;
        _resourceCursorScope = resourceCursorScope;
        _resourcePageSize = resourcePageSize;
        _resourceFingerprint = Fingerprint(Resources.Select(resource => resource.Uri));
    }

    public IReadOnlyDictionary<string, object> Capabilities => _inner.Capabilities;

    public string Instructions =>
        _inner.Instructions +
        " Filesystem access is confined to a strict local workspace sandbox with immutable artifact and store quotas.";

    public IReadOnlyList<McpResourceDefinition> Resources { get; }

    public async ValueTask<McpDispatchResult?> TryDispatchAsync(
        string method,
        JsonElement? parameters,
        JsonElement requestId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (method == "resources/list")
        {
            return HandleResourcesList(parameters, requestId);
        }
        if (method == "resources/read")
        {
            var policyRead = HandlePolicyRead(parameters, requestId);
            if (policyRead is not null)
            {
                return policyRead;
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
        if (!TryReadCursor(parameters, out var offset, out var error))
        {
            return InvalidParams(requestId, error!);
        }
        if (offset > Resources.Count)
        {
            return InvalidParams(requestId, "Cursor offset exceeds the sandbox resource catalog.");
        }

        var items = Resources.Skip(offset).Take(_resourcePageSize).ToArray();
        var nextOffset = offset + items.Length;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["resources"] = items,
        };
        if (nextOffset < Resources.Count)
        {
            result["nextCursor"] = McpCursorCodec.Encode(
                _resourceCursorScope,
                nextOffset,
                _resourceFingerprint);
        }
        return new McpDispatchResult(JsonRpcMessageWriter.Result(requestId, result));
    }

    private McpDispatchResult? HandlePolicyRead(
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
        if (!string.Equals(uriElement.GetString(), McpSandboxPolicyResource.Uri, StringComparison.Ordinal))
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
                            McpSandboxPolicyResource.Uri,
                            McpSandboxPolicyResource.MediaType,
                            _policy.Text,
                            Blob: null),
                    },
                }));
    }

    private bool TryReadCursor(
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

    private static bool HasOnlyProperties(JsonElement source, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return source.ValueKind == JsonValueKind.Object &&
               source.EnumerateObject().All(property => set.Contains(property.Name));
    }

    private static McpDispatchResult InvalidParams(JsonElement requestId, string message) =>
        new(
            JsonRpcMessageWriter.Error(
                requestId,
                JsonRpcErrorCodes.InvalidParams,
                message),
            "MCP_INVALID_PARAMS");

    private static string Fingerprint(IEnumerable<string> identifiers)
    {
        var canonical = string.Join('\n', identifiers);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
