using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

public sealed record OpenCodeSessionLifecycleOptions(
    int MaximumRequestBytes,
    int MaximumResponseBytes,
    int MaximumStatusEntries,
    int MaximumIdempotencyEntries)
{
    public const int DefaultMaximumRequestBytes = 512 * 1024;
    public const int DefaultMaximumResponseBytes = 1024 * 1024;
    public const int DefaultMaximumIdempotencyEntries = 10_000;

    public static OpenCodeSessionLifecycleOptions Default { get; } = new(
        DefaultMaximumRequestBytes,
        DefaultMaximumResponseBytes,
        OpenCodeSessionValidation.MaximumStatusEntries,
        DefaultMaximumIdempotencyEntries);

    public void Validate()
    {
        if (MaximumRequestBytes is < 1024 or > 2 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestBytes));
        }
        if (MaximumResponseBytes is < 1024 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }
        if (MaximumStatusEntries is < 1 or > OpenCodeSessionValidation.MaximumStatusEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStatusEntries));
        }
        if (MaximumIdempotencyEntries is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumIdempotencyEntries));
        }
    }
}

/// <summary>Compatibility-gated, bounded OpenCode HTTP session lifecycle adapter.</summary>
public sealed class OpenCodeSessionLifecycleClient : IOpenCodeSessionLifecycle, IAsyncDisposable
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    private static readonly string[] RequiredSessionFeatures =
    [
        OpenCodeFeatureIds.Health,
        OpenCodeFeatureIds.SessionsCreate,
        OpenCodeFeatureIds.SessionsGet,
        OpenCodeFeatureIds.SessionsStatus,
        OpenCodeFeatureIds.SessionsPromptAsync,
        OpenCodeFeatureIds.SessionsAbort,
    ];

    private readonly HttpClient _client;
    private readonly OpenCodeEndpointOptions _endpointOptions;
    private readonly OpenCodeSessionLifecycleOptions _options;
    private readonly IOpenCodeCompatibilityProbe _compatibilityProbe;
    private readonly OpenCodeSessionIdempotencyLedger _idempotency;
    private readonly SemaphoreSlim _compatibilityGate = new(1, 1);
    private readonly bool _ownsClient;
    private int _compatibilityAccepted;
    private int _disposed;

    public OpenCodeSessionLifecycleClient(
        HttpClient client,
        OpenCodeEndpointOptions endpointOptions,
        IOpenCodeCompatibilityProbe compatibilityProbe,
        OpenCodeSessionLifecycleOptions? options = null)
        : this(
            client,
            endpointOptions,
            compatibilityProbe,
            options ?? OpenCodeSessionLifecycleOptions.Default,
            ownsClient: false)
    {
    }

    private OpenCodeSessionLifecycleClient(
        HttpClient client,
        OpenCodeEndpointOptions endpointOptions,
        IOpenCodeCompatibilityProbe compatibilityProbe,
        OpenCodeSessionLifecycleOptions options,
        bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpointOptions = endpointOptions ?? throw new ArgumentNullException(nameof(endpointOptions));
        _compatibilityProbe = compatibilityProbe ?? throw new ArgumentNullException(nameof(compatibilityProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _idempotency = new OpenCodeSessionIdempotencyLedger(_options.MaximumIdempotencyEntries);
        _ownsClient = ownsClient;
    }

    public static OpenCodeSessionLifecycleClient Create(
        OpenCodeEndpointOptions endpointOptions,
        OpenCodeSessionLifecycleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpointOptions);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = endpointOptions.RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = endpointOptions.BaseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var probe = new OpenCodeCompatibilityProbe(client, endpointOptions);
        return new OpenCodeSessionLifecycleClient(
            client,
            endpointOptions,
            probe,
            options ?? OpenCodeSessionLifecycleOptions.Default,
            ownsClient: true);
    }

    public async ValueTask<OpenCodeSession> CreateSessionAsync(
        OpenCodeCreateSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        OpenCodeSessionValidation.ValidateCreateCommand(command);
        var payload = SerializeCreateCommand(command);
        EnsureRequestBound(payload);
        await EnsureCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        return await _idempotency.ExecuteAsync(
                "create",
                command.IdempotencyKey,
                payload,
                token => CreateSessionCoreAsync(payload, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<OpenCodeSession> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        OpenCodeSessionValidation.ValidateSessionId(sessionId);
        await EnsureCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(
                HttpMethod.Get,
                BuildSessionPath(sessionId),
                null,
                _options.MaximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.SessionNotFound);
        }
        RequireStatus(response.StatusCode, HttpStatusCode.OK, OpenCodeSessionErrorCodes.SessionHttpStatus);
        RequireJson(response.MediaType, OpenCodeSessionErrorCodes.SessionPayloadInvalid);
        return ParseSession(response.Payload);
    }

    public async ValueTask<IReadOnlyDictionary<string, OpenCodeSessionStatus>> GetStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(
                HttpMethod.Get,
                "session/status",
                null,
                _options.MaximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        RequireStatus(response.StatusCode, HttpStatusCode.OK, OpenCodeSessionErrorCodes.StatusHttpStatus);
        RequireJson(response.MediaType, OpenCodeSessionErrorCodes.StatusPayloadInvalid);
        try
        {
            return OpenCodeSessionStatusParser.ParseSnapshot(
                response.Payload,
                _options.MaximumStatusEntries);
        }
        catch (OpenCodeSessionStatusPayloadException)
        {
            throw new OpenCodeSessionLifecycleException(
                OpenCodeSessionErrorCodes.StatusPayloadInvalid);
        }
    }

    public async ValueTask<OpenCodePromptSubmission> SendPromptAsync(
        OpenCodeSendPromptCommand command,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        OpenCodeSessionValidation.ValidatePromptCommand(command);
        var payload = SerializePromptCommand(command);
        EnsureRequestBound(payload);
        await EnsureCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        return await _idempotency.ExecuteAsync(
                "prompt",
                command.IdempotencyKey,
                payload,
                token => SendPromptCoreAsync(command, payload, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<OpenCodeAbortResult> AbortSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        OpenCodeSessionValidation.ValidateSessionId(sessionId);
        await EnsureCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(
                HttpMethod.Post,
                BuildSessionPath(sessionId) + "/abort",
                null,
                _options.MaximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        RequireStatus(response.StatusCode, HttpStatusCode.OK, OpenCodeSessionErrorCodes.AbortHttpStatus);
        RequireJson(response.MediaType, OpenCodeSessionErrorCodes.AbortPayloadInvalid);
        try
        {
            using var document = JsonDocument.Parse(response.Payload, JsonOptions);
            if (document.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.AbortPayloadInvalid);
            }
            return new OpenCodeAbortResult(sessionId, document.RootElement.GetBoolean());
        }
        catch (OpenCodeSessionLifecycleException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.AbortPayloadInvalid);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _compatibilityGate.Dispose();
            if (_ownsClient)
            {
                _client.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }

    private async Task<OpenCodeSession> CreateSessionCoreAsync(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                HttpMethod.Post,
                "session",
                payload,
                _options.MaximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        RequireStatus(response.StatusCode, HttpStatusCode.OK, OpenCodeSessionErrorCodes.SessionHttpStatus);
        RequireJson(response.MediaType, OpenCodeSessionErrorCodes.SessionPayloadInvalid);
        return ParseSession(response.Payload);
    }

    private async Task<OpenCodePromptSubmission> SendPromptCoreAsync(
        OpenCodeSendPromptCommand command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                HttpMethod.Post,
                BuildSessionPath(command.SessionId) + "/prompt_async",
                payload,
                maximumResponseBytes: 1,
                cancellationToken)
            .ConfigureAwait(false);
        RequireStatus(response.StatusCode, HttpStatusCode.NoContent, OpenCodeSessionErrorCodes.PromptHttpStatus);
        return new OpenCodePromptSubmission(
            command.SessionId,
            command.IdempotencyKey,
            Accepted: true);
    }

    private async ValueTask EnsureCompatibilityAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _compatibilityAccepted) != 0)
        {
            return;
        }
        await _compatibilityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _compatibilityAccepted) != 0)
            {
                return;
            }
            var report = await _compatibilityProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (report.State == OpenCodeCompatibilityStates.AuthenticationRequired)
            {
                throw new OpenCodeSessionLifecycleException(
                    OpenCodeSessionErrorCodes.OpenCodeAuthenticationRequired);
            }
            if (report.State == OpenCodeCompatibilityStates.Unhealthy)
            {
                throw new OpenCodeSessionLifecycleException(
                    OpenCodeSessionErrorCodes.OpenCodeUnhealthy);
            }
            if (!report.Facts.TryGetValue("healthy", out var healthy) ||
                !string.Equals(healthy, "true", StringComparison.Ordinal))
            {
                throw new OpenCodeSessionLifecycleException(
                    OpenCodeSessionErrorCodes.OpenCodeUnavailable);
            }
            if (RequiredSessionFeatures.Any(feature =>
                    !report.DetectedFeatures.Contains(feature, StringComparer.Ordinal)))
            {
                throw new OpenCodeSessionLifecycleException(
                    OpenCodeSessionErrorCodes.OpenCodeSessionFeaturesMissing);
            }
            Volatile.Write(ref _compatibilityAccepted, 1);
        }
        finally
        {
            _compatibilityGate.Release();
        }
    }

    private async Task<BoundedHttpResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        byte[]? payload,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_endpointOptions.RequestTimeout);
        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (_endpointOptions.Username is not null && _endpointOptions.Password is not null)
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(_endpointOptions.Username + ":" + _endpointOptions.Password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        if (payload is not null)
        {
            EnsureRequestBound(payload);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        try
        {
            using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return new BoundedHttpResponse(response.StatusCode, null, []);
            }
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength > maximumResponseBytes)
            {
                throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.ResponseTooLarge);
            }
            var responsePayload = await ReadBoundedAsync(
                    response.Content,
                    maximumResponseBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            return new BoundedHttpResponse(
                response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                responsePayload);
        }
        catch (OpenCodeSessionLifecycleException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.ConnectionFailed);
        }
        catch (IOException)
        {
            throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.ConnectionFailed);
        }
    }

    private IReadOnlyDictionary<string, OpenCodeSessionStatus> ParseStatuses(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw StatusPayloadInvalid();
            }
            EnsureUniqueProperties(root, OpenCodeSessionErrorCodes.StatusPayloadInvalid);
            var result = new SortedDictionary<string, OpenCodeSessionStatus>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (result.Count >= _options.MaximumStatusEntries)
                {
                    throw StatusPayloadInvalid();
                }
                try
                {
                    OpenCodeSessionValidation.ValidateSessionId(property.Name, "providerSessionId");
                }
                catch (ArgumentException)
                {
                    throw StatusPayloadInvalid();
                }
                result.Add(property.Name, ParseStatus(property.Value));
            }
            return result;
        }
        catch (OpenCodeSessionLifecycleException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw StatusPayloadInvalid();
        }
    }

    private static OpenCodeSessionStatus ParseStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StatusPayloadInvalid();
        }
        EnsureUniqueProperties(value, OpenCodeSessionErrorCodes.StatusPayloadInvalid);
        if (!value.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            throw StatusPayloadInvalid();
        }
        var type = typeElement.GetString() ?? string.Empty;
        if (string.Equals(type, OpenCodeSessionStatusTypes.Idle, StringComparison.Ordinal))
        {
            return OpenCodeSessionStatus.Idle();
        }
        if (string.Equals(type, OpenCodeSessionStatusTypes.Busy, StringComparison.Ordinal))
        {
            return OpenCodeSessionStatus.Busy();
        }
        if (string.Equals(type, OpenCodeSessionStatusTypes.Retry, StringComparison.Ordinal))
        {
            if (!value.TryGetProperty("attempt", out var attemptElement) ||
                !attemptElement.TryGetInt32(out var attempt) ||
                attempt < 0 ||
                !value.TryGetProperty("message", out var messageElement) ||
                messageElement.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("next", out var nextElement) ||
                !nextElement.TryGetInt64(out var next) ||
                next < 0)
            {
                throw StatusPayloadInvalid();
            }
            var message = messageElement.GetString() ?? string.Empty;
            try
            {
                OpenCodeSessionValidation.ValidateProviderText(
                    message,
                    OpenCodeSessionValidation.MaximumStatusMessageBytes,
                    "providerStatusMessage",
                    allowPromptWhitespace: true);
            }
            catch (ArgumentException)
            {
                throw StatusPayloadInvalid();
            }
            return OpenCodeSessionStatus.Retry(attempt, message, next);
        }
        try
        {
            OpenCodeSessionValidation.ValidateProviderText(
                type,
                OpenCodeSessionValidation.MaximumUnknownStatusTypeBytes,
                "providerStatusType");
        }
        catch (ArgumentException)
        {
            throw StatusPayloadInvalid();
        }
        return OpenCodeSessionStatus.Unknown(type);
    }

    private static OpenCodeSession ParseSession(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw SessionPayloadInvalid();
            }
            EnsureUniqueProperties(root, OpenCodeSessionErrorCodes.SessionPayloadInvalid);
            if (!root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
            {
                throw SessionPayloadInvalid();
            }
            var id = idElement.GetString() ?? string.Empty;
            try
            {
                OpenCodeSessionValidation.ValidateSessionId(id, "providerSessionId");
            }
            catch (ArgumentException)
            {
                throw SessionPayloadInvalid();
            }

            var parentId = ReadOptionalBoundedString(
                root,
                "parentID",
                OpenCodeSessionValidation.MaximumSessionIdBytes,
                validateAsSessionId: true);
            var title = ReadOptionalBoundedString(
                root,
                "title",
                OpenCodeSessionValidation.MaximumTitleBytes,
                validateAsSessionId: false);
            long? created = null;
            long? updated = null;
            if (root.TryGetProperty("time", out var timeElement))
            {
                if (timeElement.ValueKind != JsonValueKind.Object)
                {
                    throw SessionPayloadInvalid();
                }
                EnsureUniqueProperties(timeElement, OpenCodeSessionErrorCodes.SessionPayloadInvalid);
                created = ReadOptionalNonNegativeInt64(timeElement, "created");
                updated = ReadOptionalNonNegativeInt64(timeElement, "updated");
            }
            return new OpenCodeSession(id, parentId, title, created, updated);
        }
        catch (OpenCodeSessionLifecycleException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw SessionPayloadInvalid();
        }
    }

    private static string? ReadOptionalBoundedString(
        JsonElement source,
        string propertyName,
        int maximumBytes,
        bool validateAsSessionId)
    {
        if (!source.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw SessionPayloadInvalid();
        }
        var value = element.GetString() ?? string.Empty;
        try
        {
            if (validateAsSessionId)
            {
                OpenCodeSessionValidation.ValidateSessionId(value, "providerParentSessionId");
            }
            else
            {
                OpenCodeSessionValidation.ValidateProviderText(value, maximumBytes, "providerTitle");
            }
        }
        catch (ArgumentException)
        {
            throw SessionPayloadInvalid();
        }
        return value;
    }

    private static long? ReadOptionalNonNegativeInt64(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (!element.TryGetInt64(out var value) || value < 0)
        {
            throw SessionPayloadInvalid();
        }
        return value;
    }

    private static byte[] SerializeCreateCommand(OpenCodeCreateSessionCommand command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        if (command.ParentSessionId is not null)
        {
            writer.WriteString("parentID", command.ParentSessionId);
        }
        if (command.Title is not null)
        {
            writer.WriteString("title", command.Title);
        }
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] SerializePromptCommand(OpenCodeSendPromptCommand command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("parts");
        writer.WriteStartArray();
        foreach (var part in command.Parts)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", part.Text);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
                throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.ResponseTooLarge);
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private void EnsureRequestBound(byte[] payload)
    {
        if (payload.Length > _options.MaximumRequestBytes)
        {
            throw new OpenCodeSessionLifecycleException(OpenCodeSessionErrorCodes.RequestTooLarge);
        }
    }

    private static string BuildSessionPath(string sessionId) =>
        "session/" + Uri.EscapeDataString(sessionId);

    private static void RequireStatus(
        HttpStatusCode actual,
        HttpStatusCode expected,
        string errorCode)
    {
        if (actual != expected)
        {
            throw new OpenCodeSessionLifecycleException(errorCode);
        }
    }

    private static void RequireJson(string? mediaType, string errorCode)
    {
        if (mediaType is null ||
            (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new OpenCodeSessionLifecycleException(errorCode);
        }
    }

    private static void EnsureUniqueProperties(JsonElement source, string errorCode)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new OpenCodeSessionLifecycleException(errorCode);
            }
        }
    }

    private static OpenCodeSessionLifecycleException SessionPayloadInvalid() =>
        new(OpenCodeSessionErrorCodes.SessionPayloadInvalid);

    private static OpenCodeSessionLifecycleException StatusPayloadInvalid() =>
        new(OpenCodeSessionErrorCodes.StatusPayloadInvalid);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record BoundedHttpResponse(
        HttpStatusCode StatusCode,
        string? MediaType,
        byte[] Payload);
}
