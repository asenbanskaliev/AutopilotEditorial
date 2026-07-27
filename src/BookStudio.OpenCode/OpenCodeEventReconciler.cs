using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

public sealed record OpenCodeEventReconciliationOptions(
    OpenCodeSseParserOptions Parser,
    int MaximumResponseBytes,
    int MaximumStatusEntries,
    int BoundedChannelCapacity,
    int MaximumDedupeEntries,
    TimeSpan InitialReconnectDelay,
    TimeSpan MaximumReconnectDelay,
    int MaximumConsecutiveFaults,
    TimeSpan? PeriodicPollInterval)
{
    public static OpenCodeEventReconciliationOptions Default { get; } = new(
        OpenCodeSseParserOptions.Default,
        MaximumResponseBytes: 1024 * 1024,
        MaximumStatusEntries: OpenCodeSessionValidation.MaximumStatusEntries,
        BoundedChannelCapacity: 256,
        MaximumDedupeEntries: 4096,
        InitialReconnectDelay: TimeSpan.FromMilliseconds(100),
        MaximumReconnectDelay: TimeSpan.FromSeconds(5),
        MaximumConsecutiveFaults: 8,
        PeriodicPollInterval: null);

    public TimeSpan StallTimeout => Parser.StallTimeout;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Parser);
        Parser.Validate();
        if (MaximumResponseBytes is < 1024 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }
        if (MaximumStatusEntries is < 1 or > OpenCodeSessionValidation.MaximumStatusEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStatusEntries));
        }
        if (BoundedChannelCapacity is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(BoundedChannelCapacity));
        }
        if (MaximumDedupeEntries is < 1 or > OpenCodeEventDeduplicator.MaximumDedupeEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDedupeEntries));
        }
        if (InitialReconnectDelay < TimeSpan.Zero || InitialReconnectDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(InitialReconnectDelay));
        }
        if (MaximumReconnectDelay < InitialReconnectDelay || MaximumReconnectDelay > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReconnectDelay));
        }
        if (MaximumConsecutiveFaults is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConsecutiveFaults));
        }
        if (PeriodicPollInterval is { } interval &&
            (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(PeriodicPollInterval));
        }
    }
}

/// <summary>Combines OpenCode project/global SSE with bounded status polling repair.</summary>
public sealed class OpenCodeEventReconciler : IOpenCodeEventReconciler, IAsyncDisposable
{
    private static readonly string[] RequiredFeatures =
    [
        OpenCodeFeatureIds.Health,
        OpenCodeFeatureIds.EventsProject,
        OpenCodeFeatureIds.EventsGlobal,
        OpenCodeFeatureIds.SessionsStatus,
    ];

    private readonly HttpClient _client;
    private readonly OpenCodeEndpointOptions _endpointOptions;
    private readonly OpenCodeEventReconciliationOptions _options;
    private readonly IOpenCodeCompatibilityProbe _compatibilityProbe;
    private readonly SemaphoreSlim _compatibilityGate = new(1, 1);
    private readonly bool _ownsClient;
    private int _compatibilityAccepted;
    private int _disposed;

    public OpenCodeEventReconciler(
        HttpClient client,
        OpenCodeEndpointOptions endpointOptions,
        IOpenCodeCompatibilityProbe compatibilityProbe,
        OpenCodeEventReconciliationOptions? options = null)
        : this(
            client,
            endpointOptions,
            compatibilityProbe,
            options ?? OpenCodeEventReconciliationOptions.Default,
            ownsClient: false)
    {
    }

    private OpenCodeEventReconciler(
        HttpClient client,
        OpenCodeEndpointOptions endpointOptions,
        IOpenCodeCompatibilityProbe compatibilityProbe,
        OpenCodeEventReconciliationOptions options,
        bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpointOptions = endpointOptions ?? throw new ArgumentNullException(nameof(endpointOptions));
        _compatibilityProbe = compatibilityProbe ?? throw new ArgumentNullException(nameof(compatibilityProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _ownsClient = ownsClient;
    }

    public static OpenCodeEventReconciler Create(
        OpenCodeEndpointOptions endpointOptions,
        OpenCodeEventReconciliationOptions? options = null)
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
        return new OpenCodeEventReconciler(
            client,
            endpointOptions,
            probe,
            options ?? OpenCodeEventReconciliationOptions.Default,
            ownsClient: true);
    }

    public async IAsyncEnumerable<OpenCodeReconciledEvent> WatchAsync(
        OpenCodeEventWatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        OpenCodeEventValidation.ValidateWatchRequest(request);
        await EnsureCompatibilityAsync(cancellationToken).ConfigureAwait(false);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<InternalMessage>(new BoundedChannelOptions(
            _options.BoundedChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        var pumps = new List<Task>(3);
        if (request.Scope is OpenCodeEventScopes.Project or OpenCodeEventScopes.Both)
        {
            pumps.Add(RunStreamPumpAsync(
                OpenCodeEventSources.Project,
                "event",
                projectHandshakeRequired: true,
                channel.Writer,
                lifetime.Token));
        }
        if (request.Scope is OpenCodeEventScopes.Global or OpenCodeEventScopes.Both)
        {
            pumps.Add(RunStreamPumpAsync(
                OpenCodeEventSources.Global,
                "global/event",
                projectHandshakeRequired: false,
                channel.Writer,
                lifetime.Token));
        }
        if (_options.PeriodicPollInterval is { } pollInterval)
        {
            pumps.Add(RunPeriodicPollTriggerAsync(
                pollInterval,
                channel.Writer,
                lifetime.Token));
        }
        var completion = CompleteChannelWhenPumpsFinishAsync(pumps, channel.Writer);
        var deduplicator = new OpenCodeEventDeduplicator(_options.MaximumDedupeEntries);
        var statuses = new OpenCodeBoundedStatusCache(_options.MaximumStatusEntries);
        long sequence = 0;

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                switch (message)
                {
                    case ProviderMessage provider:
                    {
                        if (!deduplicator.TryAccept(provider.Event))
                        {
                            continue;
                        }
                        if (provider.Event.Status is not null && provider.Event.SessionId is not null)
                        {
                            statuses.Set(provider.Event.SessionId, provider.Event.Status);
                        }
                        if (!ShouldEmit(provider.Event.SessionId, provider.Event.Kind, request.SessionIdFilter))
                        {
                            continue;
                        }
                        yield return ToPublicEvent(
                            ++sequence,
                            provider.Event,
                            synthetic: false,
                            reconciliationReason: null);
                        break;
                    }
                    case ReconcileMessage reconcile:
                    {
                        yield return new OpenCodeReconciledEvent(
                            ++sequence,
                            OpenCodeEventSources.Poll,
                            OpenCodeEventKinds.Reconciliation,
                            "reconciliation." + reconcile.Reason,
                            null,
                            null,
                            null,
                            null,
                            Synthetic: true,
                            reconcile.Reason,
                            Now());

                        IReadOnlyDictionary<string, OpenCodeSessionStatus> snapshot;
                        try
                        {
                            snapshot = await PollStatusesAsync(lifetime.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OpenCodeEventReconciliationException)
                        {
                            continue;
                        }
                        foreach (var pair in snapshot)
                        {
                            if (statuses.TryGet(pair.Key, out var previous) && previous == pair.Value)
                            {
                                continue;
                            }
                            statuses.Set(pair.Key, pair.Value);
                            if (request.SessionIdFilter is not null &&
                                !string.Equals(request.SessionIdFilter, pair.Key, StringComparison.Ordinal))
                            {
                                continue;
                            }
                            yield return new OpenCodeReconciledEvent(
                                ++sequence,
                                OpenCodeEventSources.Poll,
                                OpenCodeEventKinds.SessionStatus,
                                "session.status.reconciled",
                                null,
                                pair.Key,
                                null,
                                pair.Value,
                                Synthetic: true,
                                reconcile.Reason,
                                Now());
                        }
                        break;
                    }
                    case TerminalMessage terminal:
                        lifetime.Cancel();
                        throw terminal.Error;
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await Task.WhenAll(pumps).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
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

    private async Task RunStreamPumpAsync(
        string source,
        string path,
        bool projectHandshakeRequired,
        ChannelWriter<InternalMessage> writer,
        CancellationToken cancellationToken)
    {
        var faults = 0;
        var delay = _options.InitialReconnectDelay;
        var firstConnection = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            var reason = firstConnection
                ? OpenCodeReconciliationReasons.Initial
                : OpenCodeReconciliationReasons.Reconnect;
            try
            {
                using var response = await OpenStreamAsync(path, cancellationToken).ConfigureAwait(false);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var firstData = true;
                if (!projectHandshakeRequired)
                {
                    await writer.WriteAsync(new ReconcileMessage(reason), cancellationToken)
                        .ConfigureAwait(false);
                }
                await foreach (var frame in OpenCodeSseParser.ParseAsync(
                        stream,
                        _options.Parser,
                        cancellationToken).ConfigureAwait(false))
                {
                    var normalized = source == OpenCodeEventSources.Project
                        ? OpenCodeEventNormalizer.NormalizeProject(frame)
                        : OpenCodeEventNormalizer.NormalizeGlobal(frame);
                    if (firstData && projectHandshakeRequired)
                    {
                        if (normalized.Kind != OpenCodeEventKinds.Connected ||
                            !string.Equals(normalized.ProviderType, "server.connected", StringComparison.Ordinal))
                        {
                            throw new OpenCodeEventReconciliationException(
                                OpenCodeEventErrorCodes.SseProjectHandshakeInvalid);
                        }
                        await writer.WriteAsync(new ReconcileMessage(reason), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    firstData = false;
                    faults = 0;
                    delay = _options.InitialReconnectDelay;
                    await writer.WriteAsync(new ProviderMessage(normalized), cancellationToken)
                        .ConfigureAwait(false);
                }
                reason = OpenCodeReconciliationReasons.Eof;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OpenCodeEventReconciliationException exception)
            {
                reason = exception.Code switch
                {
                    OpenCodeEventErrorCodes.SseStalled => OpenCodeReconciliationReasons.Stall,
                    OpenCodeEventErrorCodes.SseUtf8Invalid or
                    OpenCodeEventErrorCodes.SsePayloadInvalid or
                    OpenCodeEventErrorCodes.SseProjectHandshakeInvalid or
                    OpenCodeEventErrorCodes.SseLineTooLarge or
                    OpenCodeEventErrorCodes.SseEventTooLarge or
                    OpenCodeEventErrorCodes.SseFieldLimitExceeded => OpenCodeReconciliationReasons.Malformed,
                    _ => OpenCodeReconciliationReasons.Reconnect,
                };
            }
            catch (HttpRequestException)
            {
                reason = OpenCodeReconciliationReasons.Reconnect;
            }
            catch (IOException)
            {
                reason = OpenCodeReconciliationReasons.Reconnect;
            }

            faults++;
            await writer.WriteAsync(new ReconcileMessage(reason), cancellationToken)
                .ConfigureAwait(false);
            if (faults >= _options.MaximumConsecutiveFaults)
            {
                await writer.WriteAsync(
                        new TerminalMessage(new OpenCodeEventReconciliationException(
                            OpenCodeEventErrorCodes.SseReconnectExhausted)),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            delay = NextDelay(delay);
            firstConnection = false;
        }
    }

    private async Task RunPeriodicPollTriggerAsync(
        TimeSpan interval,
        ChannelWriter<InternalMessage> writer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteAsync(
                    new ReconcileMessage(OpenCodeReconciliationReasons.Periodic),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task CompleteChannelWhenPumpsFinishAsync(
        IReadOnlyCollection<Task> pumps,
        ChannelWriter<InternalMessage> writer)
    {
        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private async Task<HttpResponseMessage> OpenStreamAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_endpointOptions.RequestTimeout);
        using var request = CreateGetRequest(path, "text/event-stream");
        try
        {
            var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.SseHttpStatus);
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                throw new OpenCodeEventReconciliationException(
                    OpenCodeEventErrorCodes.SseContentTypeInvalid);
            }
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.ConnectionFailed);
        }
    }

    private async Task<IReadOnlyDictionary<string, OpenCodeSessionStatus>> PollStatusesAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_endpointOptions.RequestTimeout);
        using var request = CreateGetRequest("session/status", "application/json");
        try
        {
            using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.StatusHttpStatus);
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null ||
                (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                 !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            {
                throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.StatusPayloadInvalid);
            }
            if (response.Content.Headers.ContentLength is long length &&
                length > _options.MaximumResponseBytes)
            {
                throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.ResponseTooLarge);
            }
            var payload = await ReadBoundedAsync(
                    response.Content,
                    _options.MaximumResponseBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            try
            {
                return OpenCodeSessionStatusParser.ParseSnapshot(
                    payload,
                    _options.MaximumStatusEntries);
            }
            catch (OpenCodeSessionStatusPayloadException)
            {
                throw new OpenCodeEventReconciliationException(
                    OpenCodeEventErrorCodes.StatusPayloadInvalid);
            }
        }
        catch (OpenCodeEventReconciliationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.ConnectionFailed);
        }
        catch (IOException)
        {
            throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.ConnectionFailed);
        }
    }

    private HttpRequestMessage CreateGetRequest(string path, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (_endpointOptions.Username is not null && _endpointOptions.Password is not null)
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(_endpointOptions.Username + ":" + _endpointOptions.Password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return request;
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
                throw new OpenCodeEventReconciliationException(
                    OpenCodeEventErrorCodes.OpenCodeAuthenticationRequired);
            }
            if (report.State == OpenCodeCompatibilityStates.Unhealthy)
            {
                throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.OpenCodeUnhealthy);
            }
            if (!report.Facts.TryGetValue("healthy", out var healthy) ||
                !string.Equals(healthy, "true", StringComparison.Ordinal))
            {
                throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.OpenCodeUnavailable);
            }
            if (RequiredFeatures.Any(feature =>
                    !report.DetectedFeatures.Contains(feature, StringComparer.Ordinal)))
            {
                throw new OpenCodeEventReconciliationException(
                    OpenCodeEventErrorCodes.OpenCodeEventFeaturesMissing);
            }
            Volatile.Write(ref _compatibilityAccepted, 1);
        }
        finally
        {
            _compatibilityGate.Release();
        }
    }

    private TimeSpan NextDelay(TimeSpan current)
    {
        if (current <= TimeSpan.Zero)
        {
            return _options.InitialReconnectDelay;
        }
        var doubled = TimeSpan.FromTicks(Math.Min(
            current.Ticks * 2,
            _options.MaximumReconnectDelay.Ticks));
        return doubled;
    }

    private static bool ShouldEmit(string? sessionId, string kind, string? filter)
    {
        if (filter is null || sessionId is null || kind == OpenCodeEventKinds.Connected)
        {
            return true;
        }
        return string.Equals(sessionId, filter, StringComparison.Ordinal);
    }

    private static OpenCodeReconciledEvent ToPublicEvent(
        long sequence,
        OpenCodeNormalizedProviderEvent item,
        bool synthetic,
        string? reconciliationReason) =>
        new(
            sequence,
            item.Source,
            item.Kind,
            item.ProviderType,
            item.ProviderEventId,
            item.SessionId,
            item.Directory,
            item.Status,
            synthetic,
            reconciliationReason,
            Now());

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
                throw new OpenCodeEventReconciliationException(OpenCodeEventErrorCodes.ResponseTooLarge);
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class OpenCodeBoundedStatusCache
    {
        private readonly int _capacity;
        private readonly Queue<string> _order = new();
        private readonly Dictionary<string, OpenCodeSessionStatus> _values =
            new(StringComparer.Ordinal);

        public OpenCodeBoundedStatusCache(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _capacity = capacity;
        }

        public bool TryGet(string sessionId, out OpenCodeSessionStatus? status) =>
            _values.TryGetValue(sessionId, out status);

        public void Set(string sessionId, OpenCodeSessionStatus status)
        {
            if (_values.ContainsKey(sessionId))
            {
                _values[sessionId] = status;
                return;
            }
            if (_values.Count >= _capacity)
            {
                var expired = _order.Dequeue();
                _values.Remove(expired);
            }
            _values.Add(sessionId, status);
            _order.Enqueue(sessionId);
        }
    }

    private abstract record InternalMessage;
    private sealed record ProviderMessage(OpenCodeNormalizedProviderEvent Event) : InternalMessage;
    private sealed record ReconcileMessage(string Reason) : InternalMessage;
    private sealed record TerminalMessage(OpenCodeEventReconciliationException Error) : InternalMessage;
}
