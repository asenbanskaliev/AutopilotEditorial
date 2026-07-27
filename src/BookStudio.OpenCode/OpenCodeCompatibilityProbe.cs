using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

/// <summary>Read-only OpenCode HTTP compatibility probe with bounded responses and stable failure reports.</summary>
public sealed class OpenCodeCompatibilityProbe : IOpenCodeCompatibilityProbe, IAsyncDisposable
{
    private static readonly JsonDocumentOptions HealthDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly HttpClient _client;
    private readonly OpenCodeEndpointOptions _options;
    private readonly bool _ownsClient;
    private int _disposed;

    public OpenCodeCompatibilityProbe(
        HttpClient client,
        OpenCodeEndpointOptions options)
        : this(client, options, ownsClient: false)
    {
    }

    private OpenCodeCompatibilityProbe(
        HttpClient client,
        OpenCodeEndpointOptions options,
        bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsClient = ownsClient;
    }

    public static OpenCodeCompatibilityProbe Create(OpenCodeEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = options.RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.BaseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new OpenCodeCompatibilityProbe(client, options, ownsClient: true);
    }

    public async ValueTask<OpenCodeCompatibilityReport> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var requestCount = 0;
        BoundedHttpResponse healthResponse;
        try
        {
            requestCount++;
            healthResponse = await SendGetAsync(
                    "global/health",
                    _options.MaximumHealthBytes,
                    "application/json",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OpenCodeProbeException exception)
        {
            return Report(
                OpenCodeCompatibilityStates.Unavailable,
                exception.Code,
                null,
                [],
                requestCount,
                null);
        }

        if (healthResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Report(
                OpenCodeCompatibilityStates.AuthenticationRequired,
                "authentication_required",
                null,
                [],
                requestCount,
                null);
        }
        if (!IsSuccess(healthResponse.StatusCode))
        {
            return Report(
                OpenCodeCompatibilityStates.Unavailable,
                "health_http_status",
                null,
                [],
                requestCount,
                null);
        }
        if (!IsJson(healthResponse.MediaType))
        {
            return Report(
                OpenCodeCompatibilityStates.Unavailable,
                "health_content_type_invalid",
                null,
                [],
                requestCount,
                null);
        }

        OpenCodeHealth health;
        try
        {
            health = ParseHealth(healthResponse.Payload);
        }
        catch (OpenCodeProbeException exception)
        {
            return Report(
                OpenCodeCompatibilityStates.Unavailable,
                exception.Code,
                null,
                [],
                requestCount,
                null);
        }

        var healthFeature = new[] { OpenCodeFeatureIds.Health };
        if (!health.Healthy)
        {
            return Report(
                OpenCodeCompatibilityStates.Unhealthy,
                "server_unhealthy",
                health.Version,
                healthFeature,
                requestCount,
                null);
        }

        BoundedHttpResponse specificationResponse;
        try
        {
            requestCount++;
            specificationResponse = await SendGetAsync(
                    "doc",
                    _options.MaximumSpecificationBytes,
                    "application/vnd.oai.openapi+json, application/json",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OpenCodeProbeException exception)
        {
            return Report(
                OpenCodeCompatibilityStates.Degraded,
                MapSpecificationFailure(exception.Code),
                health.Version,
                healthFeature,
                requestCount,
                null);
        }

        if (specificationResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Report(
                OpenCodeCompatibilityStates.AuthenticationRequired,
                "authentication_required",
                health.Version,
                healthFeature,
                requestCount,
                null);
        }
        if (!IsSuccess(specificationResponse.StatusCode))
        {
            return Report(
                OpenCodeCompatibilityStates.Degraded,
                "openapi_http_status",
                health.Version,
                healthFeature,
                requestCount,
                null);
        }
        if (!IsJson(specificationResponse.MediaType))
        {
            return Report(
                OpenCodeCompatibilityStates.Degraded,
                "openapi_document_unavailable",
                health.Version,
                healthFeature,
                requestCount,
                null);
        }

        OpenCodeOpenApiInspection inspection;
        try
        {
            inspection = OpenCodeOpenApiInspector.Inspect(specificationResponse.Payload);
        }
        catch (OpenCodeOpenApiException)
        {
            return Report(
                OpenCodeCompatibilityStates.Degraded,
                "openapi_document_invalid",
                health.Version,
                healthFeature,
                requestCount,
                null);
        }

        var detected = healthFeature
            .Concat(inspection.DetectedFeatures)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = OpenCodeFeatureIds.Required
            .Except(detected, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new OpenCodeCompatibilityReport(
            missing.Length == 0
                ? OpenCodeCompatibilityStates.Compatible
                : OpenCodeCompatibilityStates.Degraded,
            missing.Length == 0 ? "compatible" : "missing_required_features",
            health.Version,
            detected,
            missing,
            BuildFacts(requestCount, health.Healthy, inspection.Version));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsClient)
        {
            _client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<BoundedHttpResponse> SendGetAsync(
        string relativePath,
        int maximumBytes,
        string accept,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (_options.Username is not null && _options.Password is not null)
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(_options.Username + ":" + _options.Password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        try
        {
            using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength > maximumBytes)
            {
                throw new OpenCodeProbeException("response_too_large");
            }
            var payload = await ReadBoundedAsync(
                    response.Content,
                    maximumBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            return new BoundedHttpResponse(
                response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                payload);
        }
        catch (OpenCodeProbeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeProbeException("request_timeout");
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeProbeException("connection_failed");
        }
        catch (IOException)
        {
            throw new OpenCodeProbeException("connection_failed");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (memory.Length + read > maximumBytes)
            {
                throw new OpenCodeProbeException("response_too_large");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private static OpenCodeHealth ParseHealth(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, HealthDocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("healthy", out var healthyElement) ||
                healthyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                throw new OpenCodeProbeException("health_payload_invalid");
            }
            var version = versionElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(version) ||
                version.Length > 128 ||
                version.Any(char.IsControl))
            {
                throw new OpenCodeProbeException("health_payload_invalid");
            }
            return new OpenCodeHealth(healthyElement.GetBoolean(), version);
        }
        catch (OpenCodeProbeException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new OpenCodeProbeException("health_payload_invalid");
        }
    }

    private static OpenCodeCompatibilityReport Report(
        string state,
        string code,
        string? version,
        IReadOnlyList<string> detected,
        int requestCount,
        string? openApiVersion)
    {
        var orderedDetected = detected
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = OpenCodeFeatureIds.Required
            .Except(orderedDetected, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new OpenCodeCompatibilityReport(
            state,
            code,
            version,
            orderedDetected,
            missing,
            BuildFacts(requestCount, state != OpenCodeCompatibilityStates.Unhealthy, openApiVersion));
    }

    private static IReadOnlyDictionary<string, string> BuildFacts(
        int requestCount,
        bool healthy,
        string? openApiVersion)
    {
        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["healthy"] = healthy ? "true" : "false",
            ["requests"] = requestCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (openApiVersion is not null)
        {
            facts["openapi"] = openApiVersion;
        }
        return facts;
    }

    private static string MapSpecificationFailure(string code) =>
        code == "response_too_large"
            ? "openapi_response_too_large"
            : code;

    private static bool IsSuccess(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and <= 299;

    private static bool IsJson(string? mediaType) =>
        mediaType is not null &&
        (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private sealed record BoundedHttpResponse(
        HttpStatusCode StatusCode,
        string? MediaType,
        byte[] Payload);

    private sealed record OpenCodeHealth(bool Healthy, string Version);

    private sealed class OpenCodeProbeException : Exception
    {
        public OpenCodeProbeException(string code)
            : base("OpenCode compatibility probe failed.")
        {
            Code = code;
        }

        public string Code { get; }
    }
}
