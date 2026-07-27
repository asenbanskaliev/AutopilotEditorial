using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BookStudio.Application.OpenCode;
using BookStudio.OpenCode;

namespace BookStudio.Tests.OpenCodeSseReconciliation;

internal sealed class OpenCodeSseReconciliationJourney
{
    private readonly List<ContractualSseRequest> _requests = [];
    private int _scenarios;
    private int _events;

    public async Task<OpenCodeSseReconciliationReport> RunAsync()
    {
        await ParserFramingAsync().ConfigureAwait(false);
        await ParserBoundsAsync().ConfigureAwait(false);
        await ProjectStreamAsync().ConfigureAwait(false);
        await GlobalStreamAsync().ConfigureAwait(false);
        await DeduplicationAsync().ConfigureAwait(false);
        await EofReconnectAndPollingAsync().ConfigureAwait(false);
        await MalformedReconnectAsync().ConfigureAwait(false);
        await StallReconnectAsync().ConfigureAwait(false);
        await ReconnectExhaustionAsync().ConfigureAwait(false);
        await AuthenticationAsync().ConfigureAwait(false);
        await SessionFilterAsync().ConfigureAwait(false);
        await CancellationAndEarlyDisposalAsync().ConfigureAwait(false);

        Require(_requests.Count > 0, "SSE journey did not record HTTP requests.");
        Require(_requests.All(request => request.Method == "GET"), "NO_MUTATION gate detected a non-GET request.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "/global/health",
            "/doc",
            "/event",
            "/global/event",
            "/session/status",
        };
        Require(_requests.All(request => allowed.Contains(request.Path)), "NO_MUTATION gate detected an unplanned path.");

        return new OpenCodeSseReconciliationReport(
            _scenarios,
            _requests.Count,
            _events,
            "NO_MUTATION",
            "NO_LEAKED_TASKS");
    }

    private async Task ParserFramingAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "\uFEFF: heartbeat\r\n" +
            "event: provider\r\n" +
            "id: evt-1\r\n" +
            "retry: 250\r\n" +
            "data: {\"type\":\"custom\",\r\n" +
            "data: \"properties\":{}}\r\n\r\n" +
            "data: ignored-at-eof");
        await using var stream = new FragmentedReadStream(bytes, fragmentSize: 3);
        var frames = new List<OpenCodeSseFrame>();
        await foreach (var frame in OpenCodeSseParser.ParseAsync(stream).ConfigureAwait(false))
        {
            frames.Add(frame);
        }
        Require(frames.Count == 1, "SSE parser dispatched an unterminated event or lost the framed event.");
        Require(frames[0].Event == "provider", "SSE event field drifted.");
        Require(frames[0].Id == "evt-1", "SSE id field drifted.");
        Require(frames[0].RetryMilliseconds == 250, "SSE retry field drifted.");
        Require(
            Encoding.UTF8.GetString(frames[0].Data) == "{\"type\":\"custom\",\n\"properties\":{}}",
            "SSE multi-line data joining drifted.");
        _scenarios++;
    }

    private async Task ParserBoundsAsync()
    {
        await RequireParserCodeAsync(
            Encoding.UTF8.GetBytes("data: " + new string('x', 80) + "\n\n"),
            OpenCodeSseParserOptions.Default with { MaximumLineBytes = 64 },
            OpenCodeEventErrorCodes.SseLineTooLarge).ConfigureAwait(false);
        await RequireParserCodeAsync(
            [0x64, 0x61, 0x74, 0x61, 0x3A, 0x20, 0xC3, 0x28, 0x0A, 0x0A],
            OpenCodeSseParserOptions.Default,
            OpenCodeEventErrorCodes.SseUtf8Invalid).ConfigureAwait(false);
        await RequireParserCodeAsync(
            Encoding.UTF8.GetBytes("data: 12345\ndata: 67890\n\n"),
            OpenCodeSseParserOptions.Default with { MaximumEventDataBytes = 8 },
            OpenCodeEventErrorCodes.SseEventTooLarge).ConfigureAwait(false);
        await RequireParserCodeAsync(
            Encoding.UTF8.GetBytes("event: a\nid: b\nretry: 1\ndata: {}\n\n"),
            OpenCodeSseParserOptions.Default with { MaximumFieldCount = 3 },
            OpenCodeEventErrorCodes.SseFieldLimitExceeded).ConfigureAwait(false);
        _scenarios++;
    }

    private async Task ProjectStreamAsync()
    {
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
            ValueTask.FromResult(Route(
                request,
                project: ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n"),
                    ContractualSseChunk.Utf8("id: p-1\ndata: {\"type\":\"session.status\",\"properties\":{\"sessionID\":\"ses_project\",\"status\":{\"type\":\"busy\"}}}\n\n"),
                ]),
                status: StatusSnapshot())));
        await using var reconciler = OpenCodeEventReconciler.Create(
            Endpoint(server),
            Options(maximumFaults: 4));
        var items = await TakeAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
            4).ConfigureAwait(false);
        Require(items.Any(item => item.Kind == OpenCodeEventKinds.Connected && item.Source == OpenCodeEventSources.Project),
            "Project stream did not emit server.connected.");
        var status = items.Single(item => item.ProviderEventId == "p-1");
        Require(status.SessionId == "ses_project" && status.Status?.Type == OpenCodeSessionStatusTypes.Busy,
            "Project session.status normalization drifted.");
        RequireStrictSequence(items);
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task GlobalStreamAsync()
    {
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
            ValueTask.FromResult(Route(
                request,
                global: ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8(
                        "id: g-1\r\ndata: {\"directory\":\"/repo/book\",\"payload\":{\"type\":\"session.status\",\"properties\":{\"sessionID\":\"ses_global\",\"status\":{\"type\":\"retry\",\"attempt\":2,\"message\":\"later\",\"next\":99}}}}\r\n\r\n"),
                ], holdOpen: TimeSpan.FromSeconds(1)),
                status: StatusSnapshot())));
        await using var reconciler = OpenCodeEventReconciler.Create(Endpoint(server), Options());
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Global),
            item => item.ProviderEventId == "g-1").ConfigureAwait(false);
        var status = items.Single(item => item.ProviderEventId == "g-1");
        Require(status.Directory == "/repo/book", "Global directory normalization drifted.");
        Require(status.Status?.Type == OpenCodeSessionStatusTypes.Retry && status.Status.Attempt == 2,
            "Global retry status normalization drifted.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task DeduplicationAsync()
    {
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
            ValueTask.FromResult(Route(
                request,
                project: ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n"),
                    ContractualSseChunk.Utf8("id: duplicate\ndata: {\"type\":\"custom.event\",\"properties\":{}}\n\n"),
                    ContractualSseChunk.Utf8("id: duplicate\ndata: {\"type\":\"custom.event\",\"properties\":{}}\n\n"),
                    ContractualSseChunk.Utf8("data: {\"type\":\"fingerprint.event\",\"properties\":{}}\n\n"),
                    ContractualSseChunk.Utf8("data: {\"type\":\"fingerprint.event\",\"properties\":{}}\n\n"),
                ], holdOpen: TimeSpan.FromSeconds(1)),
                status: StatusSnapshot())));
        await using var reconciler = OpenCodeEventReconciler.Create(Endpoint(server), Options());
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
            item => item.ProviderType == "fingerprint.event").ConfigureAwait(false);
        Require(items.Count(item => item.ProviderEventId == "duplicate") == 1,
            "Event-id deduplication failed.");
        Require(items.Count(item => item.ProviderType == "fingerprint.event") == 1,
            "Payload-fingerprint deduplication failed.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task EofReconnectAndPollingAsync()
    {
        var streamCalls = 0;
        var statusCalls = 0;
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
        {
            if (request.Path == "/event")
            {
                Interlocked.Increment(ref streamCalls);
                return ValueTask.FromResult(ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n"),
                ]));
            }
            if (request.Path == "/session/status")
            {
                var call = Interlocked.Increment(ref statusCalls);
                return ValueTask.FromResult(StatusSnapshot(
                    ("ses_repair", call == 1 ? "busy" : "idle")));
            }
            return ValueTask.FromResult(Route(request));
        });
        await using var reconciler = OpenCodeEventReconciler.Create(
            Endpoint(server),
            Options(initialDelay: TimeSpan.FromMilliseconds(10), maximumDelay: TimeSpan.FromMilliseconds(20)));
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
            item => streamCalls >= 2 &&
                    item.Source == OpenCodeEventSources.Poll &&
                    item.SessionId == "ses_repair" &&
                    item.Status?.Type == OpenCodeSessionStatusTypes.Idle,
            timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Require(streamCalls >= 2, "EOF did not reconnect the project stream.");
        Require(statusCalls >= 2, "EOF/reconnect did not trigger status repair.");
        Require(items.Any(item => item.Status?.Type == OpenCodeSessionStatusTypes.Busy),
            "Initial polling repair was not emitted.");
        Require(items.Any(item => item.ReconciliationReason == OpenCodeReconciliationReasons.Eof),
            "EOF reconciliation reason was not emitted.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task MalformedReconnectAsync()
    {
        var calls = 0;
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
        {
            if (request.Path == "/event")
            {
                var call = Interlocked.Increment(ref calls);
                return ValueTask.FromResult(call == 1
                    ? ContractualSseResponse.Sse([
                        ContractualSseChunk.Utf8("data: {\"type\":\"not.connected\",\"properties\":{}}\n\n"),
                    ])
                    : ContractualSseResponse.Sse([
                        ContractualSseChunk.Utf8("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n"),
                    ], holdOpen: TimeSpan.FromSeconds(1)));
            }
            return ValueTask.FromResult(Route(request));
        });
        await using var reconciler = OpenCodeEventReconciler.Create(
            Endpoint(server),
            Options(initialDelay: TimeSpan.FromMilliseconds(10), maximumDelay: TimeSpan.FromMilliseconds(20)));
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
            item => item.Kind == OpenCodeEventKinds.Connected,
            timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Require(calls >= 2, "Malformed handshake did not reconnect.");
        Require(items.Any(item => item.ReconciliationReason == OpenCodeReconciliationReasons.Malformed),
            "Malformed reconciliation reason was not emitted.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task StallReconnectAsync()
    {
        var calls = 0;
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
        {
            if (request.Path == "/event")
            {
                var call = Interlocked.Increment(ref calls);
                return ValueTask.FromResult(call == 1
                    ? ContractualSseResponse.Sse([], holdOpen: TimeSpan.FromMilliseconds(250))
                    : ContractualSseResponse.Sse([
                        ContractualSseChunk.Utf8("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n"),
                    ], holdOpen: TimeSpan.FromSeconds(1)));
            }
            return ValueTask.FromResult(Route(request));
        });
        var parser = OpenCodeSseParserOptions.Default with { StallTimeout = TimeSpan.FromMilliseconds(60) };
        await using var reconciler = OpenCodeEventReconciler.Create(
            Endpoint(server),
            Options(parser: parser, initialDelay: TimeSpan.FromMilliseconds(10), maximumDelay: TimeSpan.FromMilliseconds(20)));
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
            item => item.Kind == OpenCodeEventKinds.Connected,
            timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Require(calls >= 2, "Stalled stream did not reconnect.");
        Require(items.Any(item => item.ReconciliationReason == OpenCodeReconciliationReasons.Stall),
            "Stall reconciliation reason was not emitted.");
        await Task.Delay(300).ConfigureAwait(false);
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task ReconnectExhaustionAsync()
    {
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
            ValueTask.FromResult(request.Path == "/event"
                ? ContractualSseResponse.Text(503, "unavailable")
                : Route(request)));
        await using var reconciler = OpenCodeEventReconciler.Create(
            Endpoint(server),
            Options(
                maximumFaults: 2,
                initialDelay: TimeSpan.FromMilliseconds(5),
                maximumDelay: TimeSpan.FromMilliseconds(10)));
        var code = string.Empty;
        try
        {
            await foreach (var _ in reconciler.WatchAsync(
                               new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project)))
            {
            }
        }
        catch (OpenCodeEventReconciliationException exception)
        {
            code = exception.Code;
        }
        Require(code == OpenCodeEventErrorCodes.SseReconnectExhausted,
            "Reconnect exhaustion code drifted.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
    }

    private async Task AuthenticationAsync()
    {
        const string expected = "Basic dXNlcjpzZWNyZXQ=";
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
        {
            if (!request.Headers.TryGetValue("Authorization", out var authorization) || authorization != expected)
            {
                return ValueTask.FromResult(ContractualSseResponse.Text(401, "unauthorized"));
            }
            return ValueTask.FromResult(Route(
                request,
                project: ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n"),
                ], holdOpen: TimeSpan.FromSeconds(1)),
                status: StatusSnapshot()));
        });
        await using var reconciler = OpenCodeEventReconciler.Create(
            Endpoint(server, "user", "secret"),
            Options());
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
            item => item.Kind == OpenCodeEventKinds.Connected).ConfigureAwait(false);
        Require(server.Requests.All(request =>
                request.Headers.TryGetValue("Authorization", out var authorization) && authorization == expected),
            "Basic Authorization was absent from compatibility, stream or polling request.");
        var serialized = JsonSerializer.Serialize(items);
        Require(!serialized.Contains("user", StringComparison.Ordinal) &&
                !serialized.Contains("secret", StringComparison.Ordinal) &&
                !serialized.Contains(expected, StringComparison.Ordinal),
            "Authentication material leaked into normalized events.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task SessionFilterAsync()
    {
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
            ValueTask.FromResult(Route(
                request,
                global: ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8(GlobalStatus("g-a", "ses_a", "busy")),
                    ContractualSseChunk.Utf8(GlobalStatus("g-b", "ses_b", "idle")),
                ], holdOpen: TimeSpan.FromSeconds(1)),
                status: StatusSnapshot(("ses_a", "busy"), ("ses_b", "idle")))));
        await using var reconciler = OpenCodeEventReconciler.Create(Endpoint(server), Options());
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Global, "ses_b"),
            item => item.ProviderEventId == "g-b").ConfigureAwait(false);
        Require(items.Any(item => item.Kind == OpenCodeEventKinds.Reconciliation),
            "Session filter hid infrastructure reconciliation events.");
        Require(items.Where(item => item.SessionId is not null).All(item => item.SessionId == "ses_b"),
            "Session filter emitted another session.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

    private async Task CancellationAndEarlyDisposalAsync()
    {
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
            ValueTask.FromResult(Route(
                request,
                project: ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n"),
                ], holdOpen: TimeSpan.FromMilliseconds(300)),
                status: StatusSnapshot())));
        await using (var reconciler = OpenCodeEventReconciler.Create(Endpoint(server), Options()))
        {
            var items = await TakeUntilAsync(
                reconciler,
                new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
                item => item.Kind == OpenCodeEventKinds.Connected).ConfigureAwait(false);
            Require(items.Count > 0, "Early-disposal scenario emitted no event.");
        }
        await Task.Delay(350).ConfigureAwait(false);
        Require(server.ActiveConnections == 0, "Early enumerator disposal leaked an SSE connection.");

        await using (var reconciler = OpenCodeEventReconciler.Create(
                         Endpoint(server),
                         Options(parser: OpenCodeSseParserOptions.Default with
                         {
                             StallTimeout = TimeSpan.FromSeconds(2),
                         })))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
        {
            var cancelled = false;
            try
            {
                await foreach (var _ in reconciler.WatchAsync(
                                   new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
                                   cancellation.Token))
                {
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Require(cancelled, "Caller cancellation did not terminate the watch.");
        }
        await Task.Delay(350).ConfigureAwait(false);
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
    }

    private static async Task RequireParserCodeAsync(
        byte[] bytes,
        OpenCodeSseParserOptions options,
        string expectedCode)
    {
        var code = string.Empty;
        try
        {
            await using var stream = new FragmentedReadStream(bytes, fragmentSize: 2);
            await foreach (var _ in OpenCodeSseParser.ParseAsync(stream, options).ConfigureAwait(false))
            {
            }
        }
        catch (OpenCodeEventReconciliationException exception)
        {
            code = exception.Code;
        }
        Require(code == expectedCode, $"Expected parser code {expectedCode}, received {code}.");
    }

    private async Task<List<OpenCodeReconciledEvent>> TakeAsync(
        OpenCodeEventReconciler reconciler,
        OpenCodeEventWatchRequest request,
        int count,
        TimeSpan? timeout = null) =>
        await TakeUntilAsync(reconciler, request, items => items.Count >= count, timeout).ConfigureAwait(false);

    private async Task<List<OpenCodeReconciledEvent>> TakeUntilAsync(
        OpenCodeEventReconciler reconciler,
        OpenCodeEventWatchRequest request,
        Func<OpenCodeReconciledEvent, bool> predicate,
        TimeSpan? timeout = null) =>
        await TakeUntilAsync(reconciler, request, items => items.Count > 0 && predicate(items[^1]), timeout)
            .ConfigureAwait(false);

    private static async Task<List<OpenCodeReconciledEvent>> TakeUntilAsync(
        OpenCodeEventReconciler reconciler,
        OpenCodeEventWatchRequest request,
        Func<List<OpenCodeReconciledEvent>, bool> predicate,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        var result = new List<OpenCodeReconciledEvent>();
        await foreach (var item in reconciler.WatchAsync(request, cancellation.Token).ConfigureAwait(false))
        {
            result.Add(item);
            if (predicate(result))
            {
                break;
            }
        }
        return result;
    }

    private static ContractualSseResponse Route(
        ContractualSseRequest request,
        ContractualSseResponse? project = null,
        ContractualSseResponse? global = null,
        ContractualSseResponse? status = null)
    {
        return request.Path switch
        {
            "/global/health" => ContractualSseResponse.Json(200, BuildHealth()),
            "/doc" => ContractualSseResponse.Json(200, BuildOpenApi()),
            "/event" => project ?? ContractualSseResponse.Text(503, "project unavailable"),
            "/global/event" => global ?? ContractualSseResponse.Text(503, "global unavailable"),
            "/session/status" => status ?? StatusSnapshot(),
            _ => ContractualSseResponse.Text(404, "missing"),
        };
    }

    private static OpenCodeEndpointOptions Endpoint(
        ContractualOpenCodeSseServer server,
        string? username = null,
        string? password = null) =>
        OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            username,
            password,
            requestTimeout: TimeSpan.FromMilliseconds(500));

    private static OpenCodeEventReconciliationOptions Options(
        OpenCodeSseParserOptions? parser = null,
        int maximumFaults = 4,
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null) =>
        new(
            parser ?? OpenCodeSseParserOptions.Default with
            {
                StallTimeout = TimeSpan.FromMilliseconds(300),
            },
            MaximumResponseBytes: 64 * 1024,
            MaximumStatusEntries: 100,
            BoundedChannelCapacity: 32,
            MaximumDedupeEntries: 128,
            InitialReconnectDelay: initialDelay ?? TimeSpan.FromMilliseconds(20),
            MaximumReconnectDelay: maximumDelay ?? TimeSpan.FromMilliseconds(50),
            MaximumConsecutiveFaults: maximumFaults,
            PeriodicPollInterval: null);

    private static byte[] BuildHealth() =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            healthy = true,
            version = "1.2.3",
        });

    private static byte[] BuildOpenApi() =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            openapi = "3.1.0",
            paths = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["/event"] = new { get = new { } },
                ["/global/event"] = new { get = new { } },
                ["/session/status"] = new { get = new { } },
            },
        });

    private static ContractualSseResponse StatusSnapshot(params (string Id, string Type)[] values)
    {
        var statuses = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            statuses[value.Id] = new { type = value.Type };
        }
        return ContractualSseResponse.Json(200, JsonSerializer.SerializeToUtf8Bytes(statuses));
    }

    private static string GlobalStatus(string id, string sessionId, string status) =>
        $"id: {id}\ndata: {{\"directory\":\"/repo\",\"payload\":{{\"type\":\"session.status\",\"properties\":{{\"sessionID\":\"{sessionId}\",\"status\":{{\"type\":\"{status}\"}}}}}}}}\n\n";

    private async Task RecordAndRequireClosedAsync(ContractualOpenCodeSseServer server)
    {
        await WaitUntilAsync(() => server.ActiveConnections == 0, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Require(server.ActiveConnections == 0, "NO_LEAKED_TASKS gate found an active server connection.");
        _requests.AddRange(server.Requests);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!predicate() && DateTime.UtcNow - started < timeout)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static void RequireStrictSequence(IReadOnlyList<OpenCodeReconciledEvent> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            Require(items[index].Sequence == index + 1, "Local event sequence is not strict and monotonic.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FragmentedReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _fragmentSize;
        private int _offset;

        public FragmentedReadStream(byte[] data, int fragmentSize)
        {
            _data = data;
            _fragmentSize = fragmentSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset >= _data.Length)
            {
                return 0;
            }
            var length = Math.Min(Math.Min(buffer.Length, _fragmentSize), _data.Length - _offset);
            _data.AsSpan(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed record OpenCodeSseReconciliationReport(
    int Scenarios,
    int Requests,
    int Events,
    string MutationGate,
    string TaskGate);
